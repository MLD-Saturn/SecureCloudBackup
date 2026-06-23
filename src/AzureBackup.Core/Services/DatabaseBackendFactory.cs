using AzureBackup.Core.Services.Backends;

namespace AzureBackup.Core.Services;

/// <summary>
/// Thin construction helper that detects the on-disk catalog format and builds
/// the appropriate backend on behalf of
/// <see cref="LocalDatabaseService.Initialize(string, ReadOnlySpan{char})"/>.
/// Kept as a separate type so <see cref="LocalDatabaseService"/> does
/// not need a <c>using</c> on the Backends namespace and so the
/// diagnostic-log wiring contract has one well-known home.
///
/// <para>
/// W4 Phase 6: routes by <see cref="CatalogFormat"/>. The new AZDB snapshot and
/// brand-new installs open directly in the <see cref="InMemorySnapshotBackend"/>;
/// a legacy SQLCipher catalog is first migrated to the snapshot format by the
/// out-of-process <see cref="LegacyMigrationOrchestrator"/> (the only place the
/// vulnerable SQLCipher engine is touched, and it runs in an isolated helper
/// process) and then opened in the snapshot backend.
/// </para>
/// </summary>
internal static class DatabaseBackendFactory
{
    /// <summary>
    /// Creates and initialises a <see cref="SqliteBackend"/> against
    /// the given database path and password.
    /// </summary>
    /// <param name="diagnosticLogSink">
    /// B13: optional event subscriber that receives the backend's
    /// diagnostic events from the moment of construction. Pre-B13
    /// the SqliteBackend was created and Initialize'd in one call,
    /// so any events fired during Initialize (notably the Argon2id
    /// KDF entry/exit/OOM events) had no subscriber and were lost.
    /// LocalDatabaseService passes its own DiagnosticLog event handler
    /// so those messages reach the file logger via the standard relay.
    /// </param>
    public static SqliteBackend CreateAndInitializeSqlite(
        string databasePath, ReadOnlySpan<char> password,
        EventHandler<string>? diagnosticLogSink = null)
    {
        // Route by on-disk format. The new application-level snapshot opens in the
        // in-memory snapshot backend (modern engine, CVE-fixed). A legacy
        // SQLCipher catalog is migrated to the snapshot format FIRST -- in an
        // isolated single-engine helper process -- and then opened in the snapshot
        // backend. A brand-new install (no file yet) also uses the snapshot
        // backend so all fresh databases are the new format.
        var format = CatalogFormat.Detect(databasePath);
        if (format == CatalogFormat.Kind.LegacySqlCipher)
        {
            diagnosticLogSink?.Invoke(null, "DatabaseBackendFactory: legacy SQLCipher catalog detected; migrating before open");
            LegacyMigrationOrchestrator.Migrate(
                databasePath, password,
                diag: msg => diagnosticLogSink?.Invoke(null, msg));
            // After a successful migration the file at databasePath is the new
            // snapshot; fall through to open it with the snapshot backend.
        }

        var backend = new InMemorySnapshotBackend();
        if (diagnosticLogSink != null)
        {
            backend.DiagnosticLog += diagnosticLogSink;
        }
        backend.Initialize(databasePath, password);
        return backend;
    }
}
