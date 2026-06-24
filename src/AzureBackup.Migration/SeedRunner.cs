using System.Text.Json;
using AzureBackup.Crypto;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Migration;

/// <summary>
/// Testable core of the helper's <c>seed</c> subcommand: reads a
/// <see cref="SeedRequest"/> from a text reader (stdin) and writes a real legacy
/// SQLCipher catalog to <see cref="SeedRequest.DatabasePath"/> (plus its
/// <c>.salt</c> sidecar). This exists ONLY so the cross-process migration
/// integration tests have a genuine SQLCipher source to migrate -- the test
/// process cannot run SQLCipher itself (it loads <c>e_sqlite3</c> via Core, which
/// disables SQLCipher; see AGENT_CONTEXT facts 61/62).
///
/// <para>
/// The database is keyed with the EXACT same Argon2id-derived passphrase +
/// <c>PRAGMA cipher_kdf_algorithm</c> / <c>kdf_iter</c> / <c>key</c> sequence that
/// <see cref="LegacyCatalogMigrator"/> reads with, so a seeded catalog is
/// genuinely unlockable by the production migrate path. The schema is the subset
/// of the modern catalog's tables (<c>config</c>, <c>files</c>, <c>chunk_index</c>)
/// the migration tests assert on; the modern backend recreates any other tables
/// (all <c>IF NOT EXISTS</c>) when it opens the migrated snapshot.
/// </para>
/// </summary>
internal static class SeedRunner
{
    /// <summary>
    /// Reads the seed request from <paramref name="stdin"/>, writes the SQLCipher
    /// catalog, and returns a <see cref="MigrationExitCode"/> as the process exit
    /// code. The password is read into a zeroable <c>char[]</c> and cleared; the
    /// raw stdin buffers are cleared, so no un-zeroable <c>string</c> copy of the
    /// secret is created.
    /// </summary>
    public static int Run(TextReader stdin, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stderr);

        SeedRequest? request;
        char[]? jsonChars = null;
        byte[]? jsonBytes = null;
        try
        {
            jsonChars = StdinRequest.ReadAll(stdin);
            if (jsonChars.Length == 0 || StdinRequest.IsAllWhitespace(jsonChars))
            {
                stderr.WriteLine("seed: no request received on stdin");
                return (int)MigrationExitCode.BadRequest;
            }

            jsonBytes = System.Text.Encoding.UTF8.GetBytes(jsonChars);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            request = JsonSerializer.Deserialize<SeedRequest>(jsonBytes, options);
            if (request is null)
            {
                stderr.WriteLine("seed: request deserialized to null");
                return (int)MigrationExitCode.BadRequest;
            }
        }
        catch (JsonException ex)
        {
            stderr.WriteLine($"seed: invalid request json -- {ex.Message}");
            return (int)MigrationExitCode.BadRequest;
        }
        finally
        {
            if (jsonChars is { Length: > 0 }) Array.Clear(jsonChars);
            if (jsonBytes is { Length: > 0 }) Array.Clear(jsonBytes);
        }

        try
        {
            Seed(request);
            stderr.WriteLine("seed: success");
            return (int)MigrationExitCode.Success;
        }
        catch (MigrationException ex)
        {
            stderr.WriteLine($"seed: failed ({ex.ExitCode}) -- {ex.Message}");
            return (int)ex.ExitCode;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"seed: unexpected error -- {ex.GetType().Name}: {ex.Message}");
            return (int)MigrationExitCode.Unexpected;
        }
        finally
        {
            request?.ClearPassword();
        }
    }

    private static void Seed(SeedRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DatabasePath) ||
            request.Password.Length == 0 ||
            string.IsNullOrWhiteSpace(request.SaltBase64))
        {
            throw new MigrationException(MigrationExitCode.BadRequest, "Seed request is missing required fields.");
        }
        if (request.FileCount < 0)
            throw new MigrationException(MigrationExitCode.BadRequest, "FileCount cannot be negative.");

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(request.SaltBase64);
        }
        catch (FormatException)
        {
            throw new MigrationException(MigrationExitCode.BadRequest, "Salt is not valid base64.");
        }
        if (salt.Length != KdfParameters.SaltSize)
            throw new MigrationException(MigrationExitCode.BadRequest,
                $"Salt must be {KdfParameters.SaltSize} bytes (got {salt.Length}).");

        SqlCipherProvider.Ensure();

        // Derive the SQLCipher unlock key with the SAME purpose string the
        // migrator reads with, so a seeded catalog is unlockable by that path.
        var sqlcipherKey = Argon2idDeriver.DeriveKey(request.Password, salt, "legacy SQLCipher unlock key");
        try
        {
            WriteSqlCipherCatalog(request.DatabasePath, sqlcipherKey, request.FileCount);
        }
        catch (MigrationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MigrationException(MigrationExitCode.WriteFailed,
                $"Failed to seed the SQLCipher catalog: {ex.Message}", ex);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(sqlcipherKey);
        }

        // Write the salt sidecar last, so the catalog + sidecar appear together.
        // The ".salt" suffix matches the convention the migrator and orchestrator
        // read with (AzureBackup.Core.CatalogPaths.SaltSuffix; not referenced here).
        File.WriteAllBytes(request.DatabasePath + ".salt", salt);
    }

    private static void WriteSqlCipherCatalog(string databasePath, byte[] sqlcipherKey, int fileCount)
    {
        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(connString);
        conn.Open();

        // Key a BRAND-NEW SQLCipher database using the SAME statement order as the
        // migrator's READ path (LegacyCatalogMigrator.ReadLegacyToPlainImage):
        // cipher_kdf_algorithm + kdf_iter FIRST, then PRAGMA key. SQLCipher derives
        // the encryption key from the KDF settings in effect when the key is
        // applied, so using the migrator's exact order guarantees the seeded key
        // and the read key are identical (a different order desynchronises them and
        // even the correct password then fails). The Argon2id key is applied as a
        // base64 PASSPHRASE with PBKDF2 set to a single iteration (our Argon2id
        // already did the strong KDF).
        using (var keyCmd = conn.CreateCommand())
        {
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

        // Force SQLCipher to encrypt the new database from page 1 BEFORE writing
        // the schema. On a fresh keyed database SQLCipher commits the cipher
        // settings to the file only when the first page is written; an explicit
        // create+drop here guarantees the resulting file is genuinely encrypted
        // (random header) rather than left as a plaintext "SQLite format 3" file.
        // (Requires a single-engine e_sqlcipher process; in a directory that also
        // has e_sqlite3 the PRAGMA key is ignored entirely -- see the test's
        // HelperPath remark.)
        using (var initCmd = conn.CreateCommand())
        {
            initCmd.CommandText = "CREATE TABLE IF NOT EXISTS _seed_init (x INTEGER); DROP TABLE _seed_init;";
            initCmd.ExecuteNonQuery();
        }

        // Create the subset of the modern catalog schema the migration tests read
        // back. Column shapes mirror SqliteBackend.Schema.CreateSchema so the
        // migrated snapshot opens cleanly and GetConfiguration / GetBackedUpFile
        // return the seeded values.
        using (var schema = conn.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS config (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    auth_method INTEGER NOT NULL DEFAULT 1,
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
                    memory_limit_enabled INTEGER NOT NULL DEFAULT 1,
                    memory_limit_mb INTEGER NULL DEFAULT NULL,
                    schema_version INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    local_path TEXT NOT NULL UNIQUE,
                    blob_name TEXT NOT NULL DEFAULT '',
                    file_size INTEGER NOT NULL,
                    last_modified TEXT NOT NULL,
                    file_hash TEXT NOT NULL DEFAULT '',
                    status INTEGER NOT NULL,
                    backed_up_at TEXT NOT NULL,
                    metadata_version INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS chunk_index (
                    chunk_hash TEXT PRIMARY KEY,
                    first_uploaded_at TEXT NOT NULL,
                    original_uploader_path TEXT NOT NULL DEFAULT '',
                    size_bytes INTEGER NOT NULL,
                    reference_count INTEGER NOT NULL,
                    current_tier INTEGER NOT NULL DEFAULT 0,
                    last_verified_at TEXT NOT NULL,
                    expected_encrypted_md5 BLOB NULL
                );
                """;
            schema.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();

        // Single config row (id = 1). Seed a password_salt so the migrated
        // catalog has a recoverable in-DB salt, mirroring a real catalog.
        using (var cfg = conn.CreateCommand())
        {
            cfg.Transaction = tx;
            cfg.CommandText = """
                INSERT INTO config (id, container_name, password_salt)
                VALUES (1, 'backup', $salt)
                ON CONFLICT(id) DO NOTHING;
                """;
            cfg.Parameters.AddWithValue("$salt", new byte[KdfParameters.SaltSize]);
            cfg.ExecuteNonQuery();
        }

        var nowUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        using (var insertFile = conn.CreateCommand())
        {
            insertFile.Transaction = tx;
            insertFile.CommandText = """
                INSERT INTO files (local_path, blob_name, file_size, last_modified,
                                   file_hash, status, backed_up_at, metadata_version)
                VALUES ($local_path, '', $file_size, $last_modified,
                        $file_hash, 1, $backed_up_at, 1);
                """;
            var localPath = insertFile.Parameters.Add("$local_path", SqliteType.Text);
            var fileSize = insertFile.Parameters.Add("$file_size", SqliteType.Integer);
            var fileHash = insertFile.Parameters.Add("$file_hash", SqliteType.Text);
            insertFile.Parameters.AddWithValue("$last_modified", nowUtc);
            insertFile.Parameters.AddWithValue("$backed_up_at", nowUtc);

            for (var i = 0; i < fileCount; i++)
            {
                localPath.Value = $"C:/data/file-{i}.dat";
                fileSize.Value = (long)i;
                fileHash.Value = $"hash-{i}";
                insertFile.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }
}
