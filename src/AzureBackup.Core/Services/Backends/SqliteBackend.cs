using System.Security.Cryptography;
using AzureBackup.Core.Models;
using Konscious.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// Option C / C-1a foundation: SQLite + SQLCipher backend skeleton.
///
/// <para>
/// This class only proves the encryption stack works end-to-end on .NET 10:
/// open an encrypted file, create the schema, close, reopen with the same
/// password, and read back. The full <c>LocalDatabaseService</c> surface is
/// implemented incrementally in C-1b onwards behind the same backend
/// abstraction so we can keep all 536 existing tests passing throughout.
/// </para>
///
/// <para>
/// Encryption: SQLCipher Community Edition via
/// <c>SQLitePCLRaw.bundle_e_sqlcipher</c>. The Argon2id-derived key is
/// passed via <c>PRAGMA key</c> using the raw-byte hex literal form
/// (<c>x'…'</c>) so SQLCipher skips its built-in PBKDF2 pass - we have
/// already done the stronger Argon2id KDF on the way in.
/// </para>
/// </summary>
internal sealed class SqliteBackend : IDatabaseBackend
{
    // Argon2id parameters - identical to LocalDatabaseService so the same
    // password derives the same key during migration.
    private const int Argon2DegreeOfParallelism = 8;
    private const int Argon2MemorySize = 65536; // 64 MB
    private const int Argon2Iterations = 3;
    private const int SaltSize = 16;
    private const int DerivedKeySize = 32;

    private SqliteConnection? _connection;
    private string? _databasePath;

    /// <summary>
    /// Salt file lives next to the database, identical convention to the
    /// LiteDB backend so an upgrading user's existing salt continues to work.
    /// </summary>
    private static string GetSaltFilePath(string databasePath) => databasePath + ".salt";

    public bool IsInitialized => _connection != null;
    public string? DatabasePath => _databasePath;

    /// <summary>
    /// Opens (or creates) the encrypted SQLite database at
    /// <paramref name="databasePath"/>. Derives the encryption key from
    /// <paramref name="password"/> using Argon2id and the stored salt; if the
    /// salt file does not exist a fresh one is generated.
    /// </summary>
    public void Initialize(string databasePath, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        _databasePath = databasePath;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var salt = LoadOrCreateSalt(databasePath);
        var derivedKey = DeriveKeyFromPassword(password, salt);
        try
        {
            OpenAndUnlock(databasePath, derivedKey);
            ApplyPragmas();
            CreateSchema();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    /// <summary>
    /// Forces any deferred writes (WAL pages) to be persisted into the main
    /// database file. Idempotent. Safe to call from any thread under the
    /// LocalDatabaseService write lock.
    /// </summary>
    public void Checkpoint()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    // ---- IndexMetadata ------------------------------------------------------

    /// <summary>
    /// Reads a timestamp value from the <c>index_metadata</c> key/value table.
    /// Values are persisted as ISO-8601 strings (round-trippable, sortable,
    /// and human-readable when poking at the DB with the sqlite3 CLI).
    /// </summary>
    public DateTime? GetIndexMetadata(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM index_metadata WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        var raw = cmd.ExecuteScalar() as string;
        if (raw == null) return null;

        // Round-trip via O so DateTimeKind.Utc survives. We always write UTC
        // values in SetIndexMetadata so this is safe.
        return DateTime.Parse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
    }

    /// <summary>
    /// Upserts a timestamp value under <paramref name="key"/>.
    /// Always normalises to UTC before persisting so reads via
    /// <see cref="GetIndexMetadata"/> are deterministic regardless of the
    /// caller's local time zone.
    /// </summary>
    public void SetIndexMetadata(string key, DateTime value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO index_metadata (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value",
            utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    // ---- Configuration ------------------------------------------------------

    // Discriminator values for watched_folder_excludes.kind. Must stay in
    // sync with SaveConfigurationCore / GetConfiguration.
    private const int ExcludeKindPattern = 0;     // WatchedFolder.ExcludePatterns
    private const int ExcludeKindSubfolder = 1;   // WatchedFolder.ExcludeSubfolders

    /// <summary>
    /// Reads the singleton config row (always row id 1) and rebuilds the
    /// nested <see cref="WatchedFolder"/> and global-exclude lists from
    /// their relational tables. Returns a default-constructed
    /// <see cref="BackupConfiguration"/> if the row was never saved (the
    /// row exists from the schema seed but every column is at its default).
    /// </summary>
    public BackupConfiguration GetConfiguration()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        var config = new BackupConfiguration();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT auth_method, storage_account_name, encrypted_connection_string,
                       container_name, password_salt, password_verification_hash,
                       last_backup_time, total_bytes_uploaded,
                       failed_login_attempts, lockout_until_ticks,
                       is_entra_id_authenticated, entra_id_user_name,
                       config_version, memory_limit_enabled, memory_limit_mb
                FROM config WHERE id = 1;
                """;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                config.AuthMethod = (AzureAuthMethod)reader.GetInt32(0);
                config.StorageAccountName = reader.IsDBNull(1) ? null : reader.GetString(1);
                config.EncryptedConnectionString = reader.IsDBNull(2)
                    ? null
                    : (byte[])reader.GetValue(2);
                config.ContainerName = reader.IsDBNull(3) ? null : reader.GetString(3);
                config.PasswordSalt = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4);
                config.PasswordVerificationHash = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5);
                config.LastBackupTime = reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6));
                config.TotalBytesUploaded = reader.GetInt64(7);
                config.FailedLoginAttempts = reader.GetInt32(8);
                config.LockoutUntilTicks = reader.IsDBNull(9) ? null : reader.GetInt64(9);
                config.IsEntraIdAuthenticated = reader.GetInt32(10) != 0;
                config.EntraIdUserName = reader.IsDBNull(11) ? null : reader.GetString(11);
                config.ConfigVersion = reader.GetInt32(12);
                config.MemoryLimitEnabled = reader.GetInt32(13) != 0;
                config.MemoryLimitMB = reader.GetInt32(14);
            }
        }

        // Watched folders (and their per-folder exclude lists).
        var foldersById = new Dictionary<long, WatchedFolder>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, path, storage_tier, is_enabled FROM watched_folders ORDER BY id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var folder = new WatchedFolder
                {
                    Path = reader.GetString(1),
                    StorageTier = (StorageTier)reader.GetInt32(2),
                    IsEnabled = reader.GetInt32(3) != 0,
                };
                foldersById[reader.GetInt64(0)] = folder;
                config.WatchedFolders.Add(folder);
            }
        }

        if (foldersById.Count > 0)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT folder_id, pattern, kind FROM watched_folder_excludes ORDER BY id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!foldersById.TryGetValue(reader.GetInt64(0), out var folder)) continue;
                var pattern = reader.GetString(1);
                var kind = reader.GetInt32(2);
                if (kind == ExcludeKindPattern) folder.ExcludePatterns.Add(pattern);
                else if (kind == ExcludeKindSubfolder) folder.ExcludeSubfolders.Add(pattern);
            }
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT pattern FROM global_exclude_patterns ORDER BY id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                config.GlobalExcludePatterns.Add(reader.GetString(0));
            }
        }

        return config;
    }

    /// <summary>
    /// Persists the singleton config row + the nested folder / pattern lists
    /// in a single transaction. Existing rows in the child tables are
    /// dropped and re-inserted (this matches LiteDB's atomic upsert
    /// semantics; the row counts here are tiny - typically &lt;20 folders
    /// and a handful of patterns - so the delete-then-insert cost is in the
    /// microseconds).
    /// </summary>
    public void SaveConfiguration(BackupConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var tx = _connection.BeginTransaction();

        // --- config row ---------------------------------------------------
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE config SET
                    auth_method = $auth_method,
                    storage_account_name = $storage_account_name,
                    encrypted_connection_string = $encrypted_connection_string,
                    container_name = $container_name,
                    password_salt = $password_salt,
                    password_verification_hash = $password_verification_hash,
                    last_backup_time = $last_backup_time,
                    total_bytes_uploaded = $total_bytes_uploaded,
                    failed_login_attempts = $failed_login_attempts,
                    lockout_until_ticks = $lockout_until_ticks,
                    is_entra_id_authenticated = $is_entra_id_authenticated,
                    entra_id_user_name = $entra_id_user_name,
                    config_version = $config_version,
                    memory_limit_enabled = $memory_limit_enabled,
                    memory_limit_mb = $memory_limit_mb
                WHERE id = 1;
                """;
            cmd.Parameters.AddWithValue("$auth_method", (int)configuration.AuthMethod);
            cmd.Parameters.AddWithValue("$storage_account_name",
                (object?)configuration.StorageAccountName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$encrypted_connection_string",
                (object?)configuration.EncryptedConnectionString ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$container_name",
                (object?)configuration.ContainerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$password_salt",
                (object?)configuration.PasswordSalt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$password_verification_hash",
                (object?)configuration.PasswordVerificationHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$last_backup_time",
                configuration.LastBackupTime.HasValue
                    ? FormatUtc(configuration.LastBackupTime.Value)
                    : DBNull.Value);
            cmd.Parameters.AddWithValue("$total_bytes_uploaded", configuration.TotalBytesUploaded);
            cmd.Parameters.AddWithValue("$failed_login_attempts", configuration.FailedLoginAttempts);
            cmd.Parameters.AddWithValue("$lockout_until_ticks",
                (object?)configuration.LockoutUntilTicks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$is_entra_id_authenticated",
                configuration.IsEntraIdAuthenticated ? 1 : 0);
            cmd.Parameters.AddWithValue("$entra_id_user_name",
                (object?)configuration.EntraIdUserName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$config_version", configuration.ConfigVersion);
            cmd.Parameters.AddWithValue("$memory_limit_enabled",
                configuration.MemoryLimitEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$memory_limit_mb", configuration.MemoryLimitMB);
            cmd.ExecuteNonQuery();
        }

        // --- watched folders (+ exclude / subfolder lists) ----------------
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = tx;
            // ON DELETE CASCADE on watched_folder_excludes drops the child
            // rows automatically; we still need an explicit clear for
            // global_exclude_patterns below.
            clear.CommandText = "DELETE FROM watched_folders;";
            clear.ExecuteNonQuery();
        }

        foreach (var folder in configuration.WatchedFolders)
        {
            long folderId;
            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO watched_folders (path, storage_tier, is_enabled)
                    VALUES ($path, $storage_tier, $is_enabled)
                    RETURNING id;
                    """;
                insert.Parameters.AddWithValue("$path", folder.Path);
                insert.Parameters.AddWithValue("$storage_tier", (int)folder.StorageTier);
                insert.Parameters.AddWithValue("$is_enabled", folder.IsEnabled ? 1 : 0);
                folderId = (long)insert.ExecuteScalar()!;
            }

            InsertExcludes(tx, folderId, folder.ExcludePatterns, ExcludeKindPattern);
            InsertExcludes(tx, folderId, folder.ExcludeSubfolders, ExcludeKindSubfolder);
        }

        // --- global excludes ----------------------------------------------
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM global_exclude_patterns;";
            clear.ExecuteNonQuery();
        }

        foreach (var pattern in configuration.GlobalExcludePatterns)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO global_exclude_patterns (pattern) VALUES ($pattern);";
            insert.Parameters.AddWithValue("$pattern", pattern);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private void InsertExcludes(SqliteTransaction tx, long folderId,
        IReadOnlyList<string> patterns, int kind)
    {
        if (patterns.Count == 0) return;
        if (_connection == null) return;

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO watched_folder_excludes (folder_id, pattern, kind)
            VALUES ($folder_id, $pattern, $kind);
            """;
        var folderParam = cmd.Parameters.AddWithValue("$folder_id", folderId);
        var patternParam = cmd.Parameters.Add("$pattern", SqliteType.Text);
        var kindParam = cmd.Parameters.AddWithValue("$kind", kind);

        foreach (var pattern in patterns)
        {
            patternParam.Value = pattern;
            cmd.ExecuteNonQuery();
        }

        // Avoid analyzer warning about unused params; reading them is a
        // no-op but documents that they are bound on the prepared statement.
        _ = folderParam;
        _ = kindParam;
    }

    private static string FormatUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return utc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DateTime ParseUtc(string value)
        => DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

    /// <summary>
    /// Closes the underlying connection and releases native handles.
    /// Safe to call multiple times.
    /// </summary>
    public void Close()
    {
        if (_connection != null)
        {
            // Force a final WAL checkpoint so the next open is fast and the
            // -wal / -shm files do not linger.
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Best-effort; never throw from Close.
            }

            _connection.Dispose();
            _connection = null;
        }
    }

    public void Dispose() => Close();

    /// <summary>
    /// Test hook: returns SQLCipher's reported version, or null if the loaded
    /// SQLite native library is not SQLCipher (i.e. encryption is silently
    /// not happening).
    /// </summary>
    internal string? ReadSqlcipherVersion()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA cipher_version;";
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Test hook: confirms the connection is open by reading SQLite's version
    /// string. Used by the C-1a smoke test to prove end-to-end open + close
    /// works without exposing the connection.
    /// </summary>
    internal string ReadSqliteVersion()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        var result = (string?)cmd.ExecuteScalar();
        return result ?? string.Empty;
    }

    /// <summary>
    /// Test hook: confirms the schema was created by counting expected tables.
    /// </summary>
    internal int CountSchemaTables()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static byte[] LoadOrCreateSalt(string databasePath)
    {
        var saltFilePath = GetSaltFilePath(databasePath);
        if (File.Exists(saltFilePath))
        {
            var salt = File.ReadAllBytes(saltFilePath);
            if (salt.Length != SaltSize)
            {
                throw new InvalidOperationException(
                    $"Database salt file is corrupted (expected {SaltSize} bytes, got {salt.Length})");
            }
            return salt;
        }

        var fresh = new byte[SaltSize];
        RandomNumberGenerator.Fill(fresh);
        File.WriteAllBytes(saltFilePath, fresh);
        return fresh;
    }

    private static byte[] DeriveKeyFromPassword(ReadOnlySpan<char> password, byte[] salt)
    {
        var passwordBytes = PasswordBytes.FromChars(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                DegreeOfParallelism = Argon2DegreeOfParallelism,
                MemorySize = Argon2MemorySize,
                Iterations = Argon2Iterations,
            };
            return argon2.GetBytes(DerivedKeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private void OpenAndUnlock(string databasePath, byte[] derivedKey)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            // Disable connection pooling. We manage exactly one connection
            // per backend instance; pooling would silently hand a previously
            // unlocked connection back to a new SqliteBackend instance,
            // bypassing the PRAGMA key check and breaking wrong-password
            // detection. Verified by an early version of the wrong-password
            // smoke test that "passed" only because the same pooled
            // connection was reused across instances.
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();

            // SQLCipher: tune the KDF BEFORE the key pragma. SQLCipher applies
            // these settings to the key-derivation step, so reordering breaks
            // it (default 256000 PBKDF2 rounds would be applied instead).
            //
            // We use PBKDF2 mode (rather than raw-key `x'...'`) because raw-key
            // mode does NOT validate the key on open - SQLCipher silently
            // decrypts pages into garbage with the wrong key. PBKDF2 mode
            // writes a verification HMAC into page 1 so wrong keys fail
            // cleanly with SQLITE_NOTADB. We set kdf_iter=1 to skip the heavy
            // PBKDF2 work because our Argon2id pass already did the strong KDF.
            using (var keyCmd = connection.CreateCommand())
            {
                keyCmd.CommandText =
                    "PRAGMA cipher_kdf_algorithm = PBKDF2_HMAC_SHA256;" +
                    "PRAGMA kdf_iter = 1;";
                keyCmd.ExecuteNonQuery();

                // Pass the derived key as a quoted base64 string. Use
                // SqliteParameter so any quote characters in the base64 output
                // (none, but defensive) cannot break the SQL.
                keyCmd.CommandText = "SELECT quote($key);";
                keyCmd.Parameters.AddWithValue("$key", Convert.ToBase64String(derivedKey));
                var quoted = (string?)keyCmd.ExecuteScalar();
                keyCmd.Parameters.Clear();
                keyCmd.CommandText = $"PRAGMA key = {quoted};";
                keyCmd.ExecuteNonQuery();
            }

            // Verify the key actually unlocked the database. With PBKDF2 mode
            // SQLCipher validates page 1's HMAC on the first physical page
            // read. ExecuteScalar on a SELECT returning zero rows does NOT
            // count - it can hit a parsed-schema cache without forcing a
            // page-1 decrypt. We use ExecuteReader and actually iterate so
            // the engine reads encrypted pages off disk; a wrong key surfaces
            // as SQLITE_NOTADB (26) at that point.
            var dbExistedBeforeOpen = new FileInfo(databasePath).Length > 0;
            if (dbExistedBeforeOpen)
            {
                try
                {
                    using var probe = connection.CreateCommand();
                    // sqlite_master always has at least one entry on a
                    // previously-initialised DB (the seeded config table).
                    probe.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                    using var reader = probe.ExecuteReader();
                    var sawAtLeastOneRow = false;
                    while (reader.Read())
                    {
                        sawAtLeastOneRow = true;
                    }
                    if (!sawAtLeastOneRow)
                    {
                        // The DB file existed but no tables were visible.
                        // Either the file is genuinely empty (impossible -
                        // CreateSchema runs on every Initialize) or the key
                        // was wrong and SQLCipher decrypted garbage that
                        // happened to parse as an empty schema.
                        connection.Dispose();
                        throw new InvalidPasswordException("Invalid password. Please try again.");
                    }
                }
                catch (SqliteException ex)
                {
                    connection.Dispose();
                    if (ex.SqliteErrorCode == 26 ||
                        (ex.Message?.Contains("not a database", StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (ex.Message?.Contains("file is encrypted", StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        throw new InvalidPasswordException("Invalid password. Please try again.", ex);
                    }
                    throw;
                }
            }

            _connection = connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ApplyPragmas()
    {
        if (_connection == null) return;

        // WAL journaling: concurrent readers + single writer, fast commits,
        // matches the rationale for Phase 5's RWLock work.
        // foreign_keys: required for ON DELETE CASCADE to work; off by default.
        // synchronous=NORMAL: safe with WAL, much faster than FULL.
        // temp_store=MEMORY: avoid disk for sort/group temporaries.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            """;
        cmd.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        if (_connection == null) return;

        // Pure-relational schema (Option C / §4 of docs/option-c-evaluation.md).
        // Ordered so foreign-key dependencies are created before their referrers.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            -- Single-row configuration table; row id is always 1.
            -- Mirrors every field on Models.BackupConfiguration (see C-1c
            -- commit message for the field-by-field mapping). Defaults match
            -- the C# model defaults so a freshly seeded row reads back as the
            -- equivalent of `new BackupConfiguration()`.
            CREATE TABLE IF NOT EXISTS config (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                auth_method INTEGER NOT NULL DEFAULT 1,            -- AzureAuthMethod, 1 = ConnectionString
                storage_account_name TEXT NULL,
                encrypted_connection_string BLOB NULL,
                container_name TEXT NULL DEFAULT 'backup',
                password_salt BLOB NULL,
                password_verification_hash BLOB NULL,
                last_backup_time TEXT NULL,
                total_bytes_uploaded INTEGER NOT NULL DEFAULT 0,
                failed_login_attempts INTEGER NOT NULL DEFAULT 0,
                lockout_until_ticks INTEGER NULL,
                is_entra_id_authenticated INTEGER NOT NULL DEFAULT 0,
                entra_id_user_name TEXT NULL,
                config_version INTEGER NOT NULL DEFAULT 3,
                memory_limit_enabled INTEGER NOT NULL DEFAULT 0,
                memory_limit_mb INTEGER NOT NULL DEFAULT 2048,
                schema_version INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS watched_folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE,
                storage_tier INTEGER NOT NULL DEFAULT 0,
                is_enabled INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS watched_folder_excludes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                folder_id INTEGER NOT NULL,
                pattern TEXT NOT NULL,
                kind INTEGER NOT NULL,  -- 0 = ExcludePatterns, 1 = ExcludeSubfolders
                FOREIGN KEY (folder_id) REFERENCES watched_folders(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_watched_folder_excludes_folder
                ON watched_folder_excludes(folder_id);

            CREATE TABLE IF NOT EXISTS global_exclude_patterns (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pattern TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                local_path TEXT NOT NULL UNIQUE,
                file_size INTEGER NOT NULL,
                last_modified TEXT NOT NULL,
                file_hash TEXT NULL,
                status INTEGER NOT NULL,
                backed_up_at TEXT NULL,
                error_message TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_files_status ON files(status);
            CREATE INDEX IF NOT EXISTS idx_files_file_hash ON files(file_hash);

            CREATE TABLE IF NOT EXISTS file_chunks (
                file_id INTEGER NOT NULL,
                chunk_order INTEGER NOT NULL,
                chunk_index INTEGER NOT NULL,
                offset INTEGER NOT NULL,
                length INTEGER NOT NULL,
                hash TEXT NOT NULL,
                blob_name TEXT NULL,
                PRIMARY KEY (file_id, chunk_order),
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_file_chunks_hash ON file_chunks(hash);

            CREATE TABLE IF NOT EXISTS pending_changes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL,
                change_type INTEGER NOT NULL,
                detected_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_pending_changes_path ON pending_changes(file_path);

            CREATE TABLE IF NOT EXISTS chunk_index (
                chunk_hash TEXT PRIMARY KEY,
                first_uploaded_at TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                reference_count INTEGER NOT NULL,
                current_tier INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_chunk_index_refcount ON chunk_index(reference_count);
            CREATE INDEX IF NOT EXISTS idx_chunk_index_tier ON chunk_index(current_tier);

            -- Reverse index built in Phase 5 / P3; carried over directly so the
            -- migration is a one-to-one row copy.
            CREATE TABLE IF NOT EXISTS chunk_file_refs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL,
                chunk_hash TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                referenced_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_chunk_file_refs_path ON chunk_file_refs(file_path);
            CREATE INDEX IF NOT EXISTS idx_chunk_file_refs_hash ON chunk_file_refs(chunk_hash);

            CREATE TABLE IF NOT EXISTS index_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Seed the singleton config row so the wrong-password probe in
        // OpenAndUnlock has a real user-table read to perform on reopen.
        // INSERT OR IGNORE keeps this idempotent across reopens; the row
        // takes its column defaults so reading immediately gives back
        // the equivalent of `new BackupConfiguration()`.
        using var seed = _connection.CreateCommand();
        seed.CommandText = """
            INSERT OR IGNORE INTO config (id) VALUES (1);
            """;
        seed.ExecuteNonQuery();
    }
}
