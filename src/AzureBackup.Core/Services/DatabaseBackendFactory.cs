using AzureBackup.Core.Services.Backends;

namespace AzureBackup.Core.Services;

/// <summary>
/// Thin construction helper that builds and initialises the SQLCipher
/// SQLite backend on behalf of
/// <see cref="LocalDatabaseService.Initialize(string, ReadOnlySpan{char})"/>.
/// Kept as a separate type so <see cref="LocalDatabaseService"/> does
/// not need a <c>using</c> on the Backends namespace and so the
/// diagnostic-log wiring contract has one well-known home.
///
/// <para>
/// W4 Phase 3 (B59): SQLite is the only backend; the historical
/// LiteDB-vs-SQLite selection logic and its <see cref="AsyncLocal{T}"/>
/// test override have been removed.
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
        var backend = new SqliteBackend();
        if (diagnosticLogSink != null)
        {
            backend.DiagnosticLog += diagnosticLogSink;
        }
        backend.Initialize(databasePath, password);
        return backend;
    }
}
