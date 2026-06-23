using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AzureBackup.Crypto;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace AzureBackup.Migration;

/// <summary>
/// Reads a legacy SQLCipher-encrypted catalog and writes the equivalent data as
/// a new AES-256-GCM-encrypted snapshot (<see cref="DbSnapshotEnvelope"/>), all
/// in this single-engine (SQLCipher-only) process so the modern engine never has
/// to coexist with SQLCipher in one process.
///
/// <para>
/// <b>No plaintext on disk.</b> The legacy data is copied row-by-row into a
/// PLAIN in-memory SQLite database (a direct <c>BackupDatabase</c> across the
/// cipher boundary is rejected by SQLCipher), then serialized to an in-memory
/// byte image and AES-256-GCM-encrypted before the ONLY disk write -- the new
/// snapshot. The plaintext image and the password/key material are zeroed.
/// </para>
/// </summary>
internal static class LegacyCatalogMigrator
{
    /// <summary>
    /// Performs the migration described by <paramref name="request"/>, writing
    /// the new encrypted snapshot to <see cref="MigrationRequest.OutputSnapshotPath"/>.
    /// Throws <see cref="MigrationException"/> with a specific
    /// <see cref="MigrationExitCode"/> on any failure.
    /// </summary>
    public static void Migrate(MigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.LegacyDatabasePath) ||
            string.IsNullOrWhiteSpace(request.OutputSnapshotPath) ||
            string.IsNullOrEmpty(request.Password) ||
            string.IsNullOrWhiteSpace(request.LegacySaltBase64))
        {
            throw new MigrationException(MigrationExitCode.BadRequest, "Migration request is missing required fields.");
        }

        if (!File.Exists(request.LegacyDatabasePath) || new FileInfo(request.LegacyDatabasePath).Length == 0)
            throw new MigrationException(MigrationExitCode.SourceNotFound,
                $"Legacy database not found or empty: {request.LegacyDatabasePath}");

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(request.LegacySaltBase64);
        }
        catch (FormatException)
        {
            throw new MigrationException(MigrationExitCode.BadRequest, "Salt is not valid base64.");
        }
        if (salt.Length != KdfParameters.SaltSize)
            throw new MigrationException(MigrationExitCode.BadRequest,
                $"Salt must be {KdfParameters.SaltSize} bytes (got {salt.Length}).");

        // Ensure the SQLCipher provider is the active engine before opening.
        EnsureSqlCipherProvider();

        // Derive the legacy SQLCipher unlock key (same KDF the app used).
        var sqlcipherKey = Argon2idDeriver.DeriveKey(request.Password, salt, "legacy SQLCipher unlock key");
        byte[]? image = null;
        try
        {
            image = ReadLegacyToPlainImage(request.LegacyDatabasePath, sqlcipherKey);

            // Encrypt the plain image into the new snapshot format and write it.
            byte[] snapshot;
            try
            {
                snapshot = DbSnapshotEnvelope.Encrypt(image, request.Password);
            }
            catch (Exception ex)
            {
                throw new MigrationException(MigrationExitCode.WriteFailed, $"Failed to encrypt snapshot: {ex.Message}", ex);
            }

            WriteAndVerifySnapshot(request.OutputSnapshotPath, snapshot, request.Password);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sqlcipherKey);
            if (image != null) CryptographicOperations.ZeroMemory(image);
        }
    }

    /// <summary>
    /// Opens the legacy SQLCipher database keyed by <paramref name="sqlcipherKey"/>,
    /// copies its schema and rows into a fresh PLAIN in-memory database, and
    /// returns the serialized image of that plain database.
    /// </summary>
    private static byte[] ReadLegacyToPlainImage(string legacyPath, byte[] sqlcipherKey)
    {
        var srcConnString = new SqliteConnectionStringBuilder
        {
            DataSource = legacyPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        using var src = new SqliteConnection(srcConnString);
        try
        {
            src.Open();
            using (var keyCmd = src.CreateCommand())
            {
                // Match the app's EXACT SQLCipher open sequence (SqliteBackend.
                // OpenAndUnlockCore). The app applies the Argon2id-derived key as
                // a PASSPHRASE (the base64 of the key string), not a raw key, with
                // SQLCipher's PBKDF2 set to a single iteration (our Argon2id
                // already did the strong KDF). Using raw-key mode (x'..hex..')
                // would produce a DIFFERENT encryption key and fail to open.
                keyCmd.CommandText =
                    "PRAGMA cipher_kdf_algorithm = PBKDF2_HMAC_SHA256;" +
                    "PRAGMA kdf_iter = 1;";
                keyCmd.ExecuteNonQuery();

                keyCmd.CommandText = "SELECT quote($key);";
                keyCmd.Parameters.AddWithValue("$key", Convert.ToBase64String(sqlcipherKey));
                var quoted = (string?)keyCmd.ExecuteScalar();
                keyCmd.Parameters.Clear();
                keyCmd.CommandText = $"PRAGMA key = {quoted};";
                keyCmd.ExecuteNonQuery();
            }

            // Force a real page-1 decrypt to validate the key; a wrong key fails
            // here as SQLITE_NOTADB(26) / SQLITE_CORRUPT(11) / OverflowException.
            List<string> tableNames;
            try
            {
                tableNames = ReadUserTableNames(src);
            }
            catch (SqliteException ex) when (IsWrongKeyError(ex))
            {
                throw new MigrationException(MigrationExitCode.InvalidPassword,
                    "The password did not unlock the legacy database.", ex);
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                throw new MigrationException(MigrationExitCode.InvalidPassword,
                    "The password did not unlock the legacy database.", ex);
            }

            using var memPlain = new SqliteConnection("Data Source=azbk-migrate-mem;Mode=Memory;Cache=Private");
            memPlain.Open();

            CopySchema(src, memPlain);
            CopyRows(src, memPlain, tableNames);

            return SerializeDatabase(memPlain);
        }
        catch (MigrationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MigrationException(MigrationExitCode.ReadFailed,
                $"Failed to read the legacy database: {ex.Message}", ex);
        }
    }

    private static List<string> ReadUserTableNames(SqliteConnection conn)
    {
        var names = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static void CopySchema(SqliteConnection src, SqliteConnection dest)
    {
        // Replay every CREATE statement (tables first so foreign keys / indexes
        // referencing them succeed), skipping SQLite's internal objects.
        var ddl = new List<string>();
        using (var cmd = src.CreateCommand())
        {
            cmd.CommandText =
                "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL " +
                "ORDER BY (type='table') DESC, rootpage;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var sql = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(sql) &&
                    !sql.Contains("sqlite_", StringComparison.OrdinalIgnoreCase))
                {
                    ddl.Add(sql);
                }
            }
        }
        foreach (var stmt in ddl)
        {
            using var cmd = dest.CreateCommand();
            cmd.CommandText = stmt;
            cmd.ExecuteNonQuery();
        }
    }

    private static void CopyRows(SqliteConnection src, SqliteConnection dest, List<string> tableNames)
    {
        using var tx = dest.BeginTransaction();
        foreach (var table in tableNames)
        {
            using var read = src.CreateCommand();
            read.CommandText = $"SELECT * FROM \"{table}\";";
            using var reader = read.ExecuteReader();

            var columns = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                columns[i] = reader.GetName(i);
            var colList = string.Join(",", columns.Select(c => $"\"{c}\""));
            var paramList = string.Join(",", Enumerable.Range(0, columns.Length).Select(i => $"$p{i}"));
            var insertSql = $"INSERT INTO \"{table}\"({colList}) VALUES({paramList});";

            while (reader.Read())
            {
                using var ins = dest.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = insertSql;
                for (var i = 0; i < reader.FieldCount; i++)
                    ins.Parameters.AddWithValue($"$p{i}", reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i));
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>
    /// Writes <paramref name="snapshot"/> atomically (temp + flush + rename) and
    /// then re-reads and decrypts it to prove the written bytes are recoverable
    /// before the launching app swaps it into place.
    /// </summary>
    private static void WriteAndVerifySnapshot(string outputPath, byte[] snapshot, string password)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var temp = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(snapshot, 0, snapshot.Length);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(temp, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch { /* best effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            throw new MigrationException(MigrationExitCode.WriteFailed, $"Failed to write snapshot: {ex.Message}", ex);
        }

        // Verify the snapshot round-trips before declaring success.
        try
        {
            var written = File.ReadAllBytes(outputPath);
            var recovered = DbSnapshotEnvelope.Decrypt(written, password);
            CryptographicOperations.ZeroMemory(recovered);
        }
        catch (Exception ex)
        {
            throw new MigrationException(MigrationExitCode.WriteFailed,
                $"Snapshot verification failed (written file does not decrypt): {ex.Message}", ex);
        }
    }

    private static byte[] SerializeDatabase(SqliteConnection connection)
    {
        var handle = GetSqliteHandle(connection);
        nint ptr = raw.sqlite3_serialize(handle, "main", out long length, 0);
        if (ptr == 0 || length <= 0)
            throw new InvalidOperationException($"sqlite3_serialize failed (ptr={ptr}, len={length}).");
        try
        {
            var image = new byte[length];
            Marshal.Copy(ptr, image, 0, checked((int)length));
            return image;
        }
        finally
        {
            raw.sqlite3_free(ptr);
        }
    }

    private static sqlite3 GetSqliteHandle(SqliteConnection connection)
    {
        var t = typeof(SqliteConnection);
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance;
        foreach (var p in t.GetProperties(Flags))
            if (typeof(sqlite3).IsAssignableFrom(p.PropertyType) && p.GetValue(connection) is sqlite3 s)
                return s;
        foreach (var f in t.GetFields(Flags))
            if (typeof(sqlite3).IsAssignableFrom(f.FieldType) && f.GetValue(connection) is sqlite3 s)
                return s;
        throw new InvalidOperationException(
            "Could not locate the sqlite3 handle on SqliteConnection (Microsoft.Data.Sqlite internals changed).");
    }

    private static bool IsWrongKeyError(SqliteException ex)
        => ex.SqliteErrorCode is 26 or 11
           || (ex.Message?.Contains("not a database", StringComparison.OrdinalIgnoreCase) ?? false)
           || (ex.Message?.Contains("file is encrypted", StringComparison.OrdinalIgnoreCase) ?? false)
           || (ex.Message?.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase) ?? false);

    private static void EnsureSqlCipherProvider()
    {
        // This process references ONLY the SQLCipher bundle, but register
        // explicitly so the active engine is deterministic regardless of
        // module-initializer order.
        raw.SetProvider(new SQLite3Provider_e_sqlcipher());
    }
}
