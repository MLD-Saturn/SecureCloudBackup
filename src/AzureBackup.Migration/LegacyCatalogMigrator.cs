using System.Security.Cryptography;
using AzureBackup.Crypto;
using AzureBackup.SqliteInterop;
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
            request.Password.Length == 0 ||
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

        // Derive the legacy SQLCipher unlock key (same KDF the app used). The
        // password is a char[] the caller zeroes; it never becomes a string here.
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

            // Force a real page-1 decrypt to validate the key BEFORE copying; a
            // wrong key fails here as SQLITE_NOTADB(26) / SQLITE_CORRUPT(11) /
            // OverflowException, which we map to InvalidPassword.
            try
            {
                ProbeKey(src);
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

            // Shared, prepared-statement row copy (encrypted source -> plain
            // in-memory dest), then serialize to a byte image.
            SqliteRowCopy.CopyInto(src, memPlain);
            return SqliteSerialization.Serialize(memPlain);
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

    /// <summary>
    /// Forces a real page-1 decrypt so a wrong SQLCipher key surfaces (as
    /// SQLITE_NOTADB / SQLITE_CORRUPT) BEFORE the row copy begins, letting the
    /// caller map it to <see cref="MigrationExitCode.InvalidPassword"/>.
    /// </summary>
    private static void ProbeKey(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) { /* iterate to force page reads */ }
    }

    /// <summary>
    /// Writes <paramref name="snapshot"/> atomically (temp + flush + rename) and
    /// then re-reads and decrypts it to prove the written bytes are recoverable
    /// before the launching app swaps it into place.
    /// </summary>
    private static void WriteAndVerifySnapshot(string outputPath, byte[] snapshot, ReadOnlySpan<char> password)
    {
        try
        {
            AtomicFile.WriteAllBytesAtomic(outputPath, snapshot);
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

    private static bool IsWrongKeyError(SqliteException ex)
        => ex.SqliteErrorCode is 26 or 11
           || (ex.Message?.Contains("not a database", StringComparison.OrdinalIgnoreCase) ?? false)
           || (ex.Message?.Contains("file is encrypted", StringComparison.OrdinalIgnoreCase) ?? false)
           || (ex.Message?.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase) ?? false);

    private static readonly object ProviderGate = new();
    private static bool _providerRegistered;

    private static void EnsureSqlCipherProvider()
    {
        // This process references ONLY the SQLCipher bundle, but register
        // explicitly (once) so the active engine is deterministic regardless of
        // module-initializer order.
        if (_providerRegistered) return;
        lock (ProviderGate)
        {
            if (_providerRegistered) return;
            raw.SetProvider(new SQLite3Provider_e_sqlcipher());
            _providerRegistered = true;
        }
    }
}
