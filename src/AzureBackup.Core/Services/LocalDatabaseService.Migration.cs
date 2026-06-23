namespace AzureBackup.Core.Services;

using AzureBackup.Core.Services.Backends;

/// <summary>
/// Static helpers for catalog-database existence probing and B50
/// non-destructive quarantine of an unreadable catalog. The legacy
/// LiteDB migration / upgrade helpers that used to live here were
/// removed in W4 Phase 2 (B58) once production code stopped calling
/// them; only the two SQLite-only entry points the active codebase
/// still relies on are kept.
/// </summary>
public partial class LocalDatabaseService
{
    /// <summary>
    /// Checks if a usable encrypted database file exists at the given
    /// path. Used to decide between first-run setup and unlock prompt.
    /// </summary>
    /// <remarks>
    /// W4 Phase 6: a catalog "exists" when it is recognised by
    /// <see cref="CatalogFormat"/> as either the new application-level
    /// AES-256-GCM snapshot (AZDB magic header) or a legacy SQLCipher
    /// database (a non-AZDB file with a <c>.salt</c> sidecar). The legacy
    /// case is migrated to the snapshot format automatically on the next
    /// unlock, so both formats route the user to the unlock prompt rather
    /// than first-run setup. A 0-byte stub or an unrecognised file returns
    /// false, routing the user into first-run setup (which cleans up any
    /// stale artefacts).
    /// </remarks>
    public static bool DatabaseExists(string databasePath)
        => CatalogFormat.Detect(databasePath) != CatalogFormat.Kind.Missing;

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
