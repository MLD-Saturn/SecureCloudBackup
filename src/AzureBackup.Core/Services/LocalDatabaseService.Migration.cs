using System.Security.Cryptography;
using LiteDB;

namespace AzureBackup.Core.Services;

/// <summary>
/// Static methods for database existence checks and format migration.
/// </summary>
public partial class LocalDatabaseService
{
    /// <summary>
    /// Checks if a usable encrypted database file exists at the given
    /// path. Used to decide between first-run setup and unlock prompt.
    /// </summary>
    /// <remarks>
    /// B10 hardening: the pre-fix implementation was just <c>File.Exists</c>,
    /// which meant a 0-byte stub file (e.g. left behind by a partial
    /// pre-fix unlock attempt that crashed after <c>connection.Open()</c>
    /// but before any pages were written) would mis-route the user into
    /// the unlock prompt forever. We now require:
    /// <list type="number">
    ///   <item>The .db file exists.</item>
    ///   <item>The .salt sidecar exists (no salt = no key derivation).</item>
    ///   <item>The .db is at least 4096 bytes (one SQLite page; an
    ///         empty/stub file fails this).</item>
    /// </list>
    /// Returning false routes the user into first-run setup, where
    /// CleanupStaleArtifacts (called from the same path) will TrySecureDelete
    /// any stub files so the next launch starts truly fresh.
    /// </remarks>
    public static bool DatabaseExists(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) return false;
        if (!File.Exists(databasePath)) return false;

        // SQLite header is 100 bytes; a freshly-keyed file with one
        // table is ~32 KB. 4096 (one page) is a defensive lower bound
        // that catches genuine stub files without false-negative-ing
        // a real DB.
        try
        {
            var len = new FileInfo(databasePath).Length;
            if (len < 4096) return false;
        }
        catch
        {
            return false;
        }

        // No salt = no possible decryption. Treat as not-a-database.
        if (!File.Exists(GetSaltFilePath(databasePath))) return false;

        return true;
    }

    /// <summary>
    /// Checks if a database has an associated Argon2id salt file.
    /// Databases without a salt file are using the legacy encryption method.
    /// </summary>
    /// <param name="databasePath">Path to the database file</param>
    /// <returns>True if the database has a salt file (using new Argon2id encryption)</returns>
    public static bool HasArgon2idSalt(string databasePath)
    {
        var saltPath = GetSaltFilePath(databasePath);
        return File.Exists(saltPath);
    }

    /// <summary>
    /// Checks if an existing database uses the legacy encryption method (raw password without Argon2id).
    /// Legacy databases exist but have no .salt file.
    /// </summary>
    /// <param name="databasePath">Path to the database file</param>
    /// <returns>True if the database exists and uses legacy encryption</returns>
    public static bool IsLegacyEncryptedDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
            return false;
        
        // If it's unencrypted, it's not legacy encrypted
        if (IsUnencryptedDatabase(databasePath))
            return false;
        
        // If it has a salt file, it's using new Argon2id encryption
        if (HasArgon2idSalt(databasePath))
            return false;
        
        // Database exists, is encrypted, but has no salt file = legacy encryption
        return true;
    }

    /// <summary>
    /// Checks if an existing database is unencrypted (legacy format).
    /// Used to detect if migration is needed.
    /// </summary>
    /// <param name="databasePath">Path to the database file</param>
    /// <returns>True if the database exists and is unencrypted</returns>
    public static bool IsUnencryptedDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
            return false;

        try
        {
            // Try to open without password - if it works, it's unencrypted
            using var db = new LiteDatabase(databasePath);
            // Try to read something to verify it's a valid database
            var _ = db.GetCollectionNames().ToList();
            return true;
        }
        catch
        {
            // Either not a database or is encrypted
            return false;
        }
    }

    /// <summary>
    /// Migrates an unencrypted database to an encrypted one.
    /// Creates a new encrypted database with Argon2id key derivation and copies all data.
    /// </summary>
    /// <param name="sourcePath">Path to the unencrypted database</param>
    /// <param name="targetPath">Path for the new encrypted database</param>
    /// <param name="password">Password to encrypt the new database. Span overload so the
    /// plaintext can remain in a caller-owned <c>char[]</c> and be zeroed after use.</param>
    public static void MigrateToEncrypted(string sourcePath, string targetPath, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source database not found", sourcePath);

        // Open source (unencrypted)
        using var sourceDb = new LiteDatabase(sourcePath);
        CopyToEncryptedDatabase(sourceDb, targetPath, password);
    }

    /// <summary>
    /// Legacy <c>string</c> overload of <see cref="MigrateToEncrypted(string, string, ReadOnlySpan{char})"/>.
    /// </summary>
    public static void MigrateToEncrypted(string sourcePath, string targetPath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        MigrateToEncrypted(sourcePath, targetPath, password.AsSpan());
    }

    /// <summary>
    /// Migrates a legacy encrypted database (raw password, no Argon2id) to the new format.
    /// Creates a new database with Argon2id key derivation and copies all data.
    /// </summary>
    /// <param name="sourcePath">Path to the legacy encrypted database</param>
    /// <param name="targetPath">Path for the new encrypted database</param>
    /// <param name="password">Password (same password used for the legacy database). Span overload
    /// so the plaintext can remain in a caller-owned <c>char[]</c> and be zeroed after use.</param>
    /// <exception cref="InvalidPasswordException">Thrown if password is incorrect for the legacy database</exception>
    public static void MigrateLegacyEncrypted(string sourcePath, string targetPath, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source database not found", sourcePath);

        // Legacy LiteDB API only accepts string passwords. The string is short-lived
        // (scoped to this method) and this legacy migration path runs at most once per
        // user — after the upgrade the Argon2id path uses a derived-key Base64 string,
        // never the user's plaintext password.
        var legacyPasswordString = new string(password);
        // Open source with legacy encryption (raw password)
        var sourceConnString = new ConnectionString
        {
            Filename = sourcePath,
            Password = legacyPasswordString, // Raw password - legacy method
            Connection = ConnectionType.Shared
        };

        LiteDatabase sourceDb;
        try
        {
            sourceDb = new LiteDatabase(sourceConnString);
            // Verify we can actually read from it
            _ = sourceDb.GetCollectionNames().ToList();
        }
        catch (LiteException ex) when (ex.Message.Contains("invalid password", StringComparison.OrdinalIgnoreCase) ||
                                        ex.Message.Contains("file is not a valid", StringComparison.OrdinalIgnoreCase) ||
                                        ex.Message.Contains("HMAC", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidPasswordException("Invalid password for legacy database. Please try again.", ex);
        }

        using (sourceDb)
        {
            CopyToEncryptedDatabase(sourceDb, targetPath, password);
        }
    }

    /// <summary>
    /// Legacy <c>string</c> overload of <see cref="MigrateLegacyEncrypted(string, string, ReadOnlySpan{char})"/>.
    /// </summary>
    public static void MigrateLegacyEncrypted(string sourcePath, string targetPath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        MigrateLegacyEncrypted(sourcePath, targetPath, password.AsSpan());
    }

    /// <summary>
    /// Creates a new Argon2id-encrypted database and copies all collections from the source.
    /// Shared by <see cref="MigrateToEncrypted(string, string, ReadOnlySpan{char})"/> and
    /// <see cref="MigrateLegacyEncrypted(string, string, ReadOnlySpan{char})"/>.
    /// </summary>
    private static void CopyToEncryptedDatabase(LiteDatabase sourceDb, string targetPath, ReadOnlySpan<char> password)
    {
        // Generate salt for the new encrypted database
        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        // Save the salt file
        var saltFilePath = GetSaltFilePath(targetPath);
        File.WriteAllBytes(saltFilePath, salt);

        // Derive strong key using Argon2id
        var derivedKey = DeriveKeyFromPassword(password, salt);

        try
        {
            var dbPassword = Convert.ToBase64String(derivedKey);

            // Create target (encrypted with derived key)
            var targetConnString = new ConnectionString
            {
                Filename = targetPath,
                Password = dbPassword,
                Connection = ConnectionType.Shared
            };
            using var targetDb = new LiteDatabase(targetConnString);

            // Copy all collections
            foreach (var collectionName in sourceDb.GetCollectionNames())
            {
                var sourceCollection = sourceDb.GetCollection(collectionName);
                var targetCollection = targetDb.GetCollection(collectionName);

                foreach (var doc in sourceCollection.FindAll())
                {
                    targetCollection.Insert(doc);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    /// <summary>
    /// Crash-safe atomic swap of a freshly migrated database into place.
    /// Performs the four-step rename
    /// (<c>db -&gt; .bak</c>, <c>tmp -&gt; db</c>, <c>tmp.salt -&gt; db.salt</c>,
    /// <c>delete .bak</c>) under cover of a sentinel file so a process crash
    /// at any step is recoverable from disk state alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pre-fix the legacy unencrypted-to-encrypted and legacy-encrypted-to-Argon2id
    /// upgrade paths used three bare <see cref="File.Move"/> calls. A crash
    /// between the first and second move left the original database renamed
    /// to <c>.bak</c> with no encrypted file in place — the user would launch
    /// the app, see "no database found", and lose access to all backed-up
    /// state. There was no on-disk hint that a migration had been in flight.
    /// </para>
    /// <para>
    /// Sentinel layout: a JSON file at
    /// <c>{databasePath}.upgrade-pending</c> capturing the source / temp /
    /// backup paths. Created BEFORE the first rename, deleted AFTER the
    /// final cleanup. <see cref="RecoverInterruptedUpgrade"/> consumes it
    /// on next startup.
    /// </para>
    /// </remarks>
    /// <param name="databasePath">Final destination path (e.g. <c>backup.db</c>).</param>
    /// <param name="tempPath">Path of the freshly migrated file
    /// (e.g. <c>backup.db.encrypted</c> or <c>.upgraded</c>). Must exist.</param>
    /// <param name="backupSuffix">Suffix to give the original file while it is
    /// preserved as a fallback (e.g. <c>.unencrypted.bak</c> or <c>.legacy.bak</c>).</param>
    public static void CommitDatabaseUpgrade(string databasePath, string tempPath, string backupSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupSuffix);
        if (!File.Exists(tempPath))
            throw new FileNotFoundException("Temp database file does not exist.", tempPath);

        var backupPath = databasePath + backupSuffix;
        var tempSaltPath = tempPath + ".salt";
        var finalSaltPath = databasePath + ".salt";
        var sentinelPath = databasePath + ".upgrade-pending";

        // Write the sentinel BEFORE any rename so a crash mid-rename can be
        // diagnosed. JSON is intentionally simple so RecoverInterruptedUpgrade
        // can parse without a JSON deserializer dependency.
        File.WriteAllText(sentinelPath,
            $"{{\"databasePath\":\"{Escape(databasePath)}\",\"tempPath\":\"{Escape(tempPath)}\",\"backupPath\":\"{Escape(backupPath)}\"}}");

        try
        {
            // Step 1: original -> .bak (preserve fallback).
            if (File.Exists(databasePath))
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(databasePath, backupPath);
            }

            // Step 2: temp -> final.
            File.Move(tempPath, databasePath);

            // Step 3: salt move (best-effort; salt may not exist for some
            // migration variants).
            if (File.Exists(tempSaltPath))
            {
                if (File.Exists(finalSaltPath)) File.Delete(finalSaltPath);
                File.Move(tempSaltPath, finalSaltPath);
            }

            // Step 4: cleanup .bak. Only after everything else committed.
            // .bak holds the previous-encryption (or unencrypted) database
            // contents -- secret-bearing, so route through TrySecureDelete.
            FileSystemHelper.TrySecureDelete(backupPath);
        }
        finally
        {
            // Always remove the sentinel last. If we got here via exception
            // mid-rename, the sentinel STAYS so RecoverInterruptedUpgrade
            // can finish the job on next launch. Only delete on the happy
            // path where every step above succeeded. Sentinel is a tiny
            // JSON path list -- non-secret, so plain TryDelete.
            if (File.Exists(databasePath) && !File.Exists(tempPath) && !File.Exists(backupPath))
            {
                FileSystemHelper.TryDelete(sentinelPath);
            }
        }

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Completes a previously-interrupted <see cref="CommitDatabaseUpgrade"/>
    /// by examining the sentinel file at <c>{databasePath}.upgrade-pending</c>.
    /// Idempotent and safe to call on every startup before the database is opened.
    /// Returns <c>true</c> if recovery work was performed.
    /// </summary>
    /// <remarks>
    /// Recovery logic mirrors the four crash points of <see cref="CommitDatabaseUpgrade"/>:
    /// <list type="bullet">
    ///   <item>Crash before step 1 — temp + original both present, sentinel
    ///     present. Rerun the swap from step 1.</item>
    ///   <item>Crash between steps 1 and 2 — original is gone (renamed to
    ///     .bak), temp present. Rerun from step 2.</item>
    ///   <item>Crash between steps 2 and 4 — final present, .bak still
    ///     present. Rerun salt + cleanup.</item>
    ///   <item>Sentinel orphaned (final + no temp + no .bak) — just delete
    ///     it.</item>
    /// </list>
    /// </remarks>
    public static bool RecoverInterruptedUpgrade(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var sentinelPath = databasePath + ".upgrade-pending";
        if (!File.Exists(sentinelPath)) return false;

        // Parse the sentinel JSON without bringing in a deserializer. The
        // file is written by CommitDatabaseUpgrade with a fixed shape.
        var raw = File.ReadAllText(sentinelPath);
        var tempPath = ExtractField(raw, "tempPath") ?? throw new InvalidDataException(
            $"Upgrade sentinel at {sentinelPath} is missing tempPath.");
        var backupPath = ExtractField(raw, "backupPath") ?? throw new InvalidDataException(
            $"Upgrade sentinel at {sentinelPath} is missing backupPath.");

        var tempSaltPath = tempPath + ".salt";
        var finalSaltPath = databasePath + ".salt";

        try
        {
            // Case A: temp file is still present — finish the swap.
            if (File.Exists(tempPath))
            {
                // Make room for the temp -> final move.
                if (File.Exists(databasePath))
                {
                    // Database wasn't renamed to .bak yet; do it now (preserves the
                    // pre-upgrade copy as the fallback).
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    File.Move(databasePath, backupPath);
                }
                File.Move(tempPath, databasePath);

                if (File.Exists(tempSaltPath))
                {
                    if (File.Exists(finalSaltPath)) File.Delete(finalSaltPath);
                    File.Move(tempSaltPath, finalSaltPath);
                }
            }

            // Case B: cleanup leftover .bak now that the new file is in place.
            // .bak holds the previous-encryption (or unencrypted) database
            // contents -- secret-bearing, so route through TrySecureDelete.
            if (File.Exists(databasePath) && File.Exists(backupPath))
            {
                FileSystemHelper.TrySecureDelete(backupPath);
            }

            return true;
        }
        finally
        {
            // Sentinel cleared only when every artifact is in its final state.
            if (File.Exists(databasePath) && !File.Exists(tempPath) && !File.Exists(backupPath))
            {
                FileSystemHelper.TryDelete(sentinelPath);
            }
        }

        static string? ExtractField(string json, string key)
        {
            // Minimal scanner: find "key":"value" with simple backslash unescape.
            var needle = "\"" + key + "\":\"";
            var i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i += needle.Length;
            var sb = new System.Text.StringBuilder();
            for (; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    sb.Append(json[++i]);
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// B50: moves a corrupt catalog database file (and its companion
    /// <c>-wal</c>, <c>-shm</c>, <c>-journal</c>, and <c>.salt</c>
    /// artefacts) into a timestamped <c>.quarantine-yyyyMMdd-HHmmss</c>
    /// suffix beside the original. The quarantined files are NOT
    /// deleted -- they are kept on disk so the user (or a support
    /// engineer) can retrieve them later for forensic analysis or to
    /// confirm there is nothing salvageable. The next call to
    /// <see cref="Initialize(string, ReadOnlySpan{char})"/> against
    /// <paramref name="databasePath"/> will create a fresh database
    /// with a fresh salt because <see cref="DatabaseExists"/> returns
    /// false once the original artefacts have been moved aside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="SecureReset"/> on purpose:
    /// <c>SecureReset</c> destroys the catalog because the user
    /// explicitly asked to start over. Quarantine is the recovery
    /// path for an UNREADABLE catalog that the user did NOT ask to
    /// delete; we keep the bytes so a future tool (or the user) can
    /// inspect them. Because the quarantined database remains
    /// SQLCipher-encrypted on disk, retaining it is no worse from a
    /// secrets-exposure standpoint than the original file was.
    /// </para>
    ///
    /// <para>
    /// The encrypted connection string lives inside the quarantined
    /// catalog; it cannot be recovered without unlocking that catalog.
    /// The recovery flow forces the user to re-enter the connection
    /// string by hand once the fresh catalog is created. Storage
    /// account name, container name, watched folders, and exclusion
    /// patterns must also be re-entered because they live in the same
    /// encrypted catalog.
    /// </para>
    ///
    /// <para>
    /// Every File.Move is wrapped in a try/catch so a single locked
    /// companion file (e.g. an antivirus scan holding <c>-wal</c>
    /// open) cannot leave the operation half-done; we move what we
    /// can and report what we could not. Returns the absolute path of
    /// the quarantined main database file (the <c>.quarantine-...</c>
    /// path) so the caller can surface it to the user.
    /// </para>
    /// </remarks>
    /// <param name="databasePath">
    /// Path to the corrupt catalog database file (the same path that
    /// would be passed to <see cref="Initialize(string, ReadOnlySpan{char})"/>).
    /// </param>
    /// <returns>
    /// Result describing what was moved and any companion files that
    /// could not be moved.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="databasePath"/> is null or
    /// whitespace.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the main database file does not exist on disk.
    /// </exception>
    public static QuarantineResult QuarantineCorruptDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                "Cannot quarantine a database file that does not exist on disk.",
                databasePath);
        }

        // Single timestamp for every artefact so the quarantine set
        // is identifiable as belonging to one event. UTC for sort
        // stability across DST transitions.
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var suffix = ".quarantine-" + stamp;

        var quarantinedMain = databasePath + suffix;
        var movedFiles = new List<string>();
        var skippedFiles = new List<string>();

        // Move the main database file FIRST. If this fails the whole
        // operation has no useful effect, so let the exception
        // propagate; the caller will surface it.
        File.Move(databasePath, quarantinedMain);
        movedFiles.Add(quarantinedMain);

        // Companion files are best-effort: if a virus scanner has
        // -wal pinned we still want the main + salt out of the way so
        // the next Initialize creates a clean DB.
        TryMoveCompanion(databasePath + "-wal", suffix, movedFiles, skippedFiles);
        TryMoveCompanion(databasePath + "-shm", suffix, movedFiles, skippedFiles);
        TryMoveCompanion(databasePath + "-journal", suffix, movedFiles, skippedFiles);
        TryMoveCompanion(GetSaltFilePath(databasePath), suffix, movedFiles, skippedFiles);

        return new QuarantineResult(
            QuarantinedDatabasePath: quarantinedMain,
            MovedFiles: movedFiles,
            SkippedFiles: skippedFiles);

        static void TryMoveCompanion(
            string sourcePath, string suffix,
            List<string> moved, List<string> skipped)
        {
            if (!File.Exists(sourcePath)) return;
            var target = sourcePath + suffix;
            try
            {
                File.Move(sourcePath, target);
                moved.Add(target);
            }
            catch (Exception ex)
            {
                skipped.Add($"{sourcePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// B50: result of <see cref="LocalDatabaseService.QuarantineCorruptDatabase"/>.
/// </summary>
/// <param name="QuarantinedDatabasePath">
/// Absolute path of the renamed main database file, e.g.
/// <c>C:\Users\me\AppData\Roaming\AzureBackup\backup.db.quarantine-20260427-153012</c>.
/// </param>
/// <param name="MovedFiles">
/// Every file that was successfully moved aside (the main DB plus
/// any companion <c>-wal</c>, <c>-shm</c>, <c>-journal</c>, or salt
/// file that existed). The main DB is always at index 0.
/// </param>
/// <param name="SkippedFiles">
/// Companion files whose move failed (typically due to another
/// process holding a handle). Each entry is a single line containing
/// the path and the exception type+message. Empty on the happy path.
/// </param>
public sealed record QuarantineResult(
    string QuarantinedDatabasePath,
    IReadOnlyList<string> MovedFiles,
    IReadOnlyList<string> SkippedFiles);
