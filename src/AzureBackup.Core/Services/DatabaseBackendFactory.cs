using AzureBackup.Core.Services.Backends;

namespace AzureBackup.Core.Services;

/// <summary>
/// Decides at
/// <see cref="LocalDatabaseService.Initialize(string, ReadOnlySpan{char})"/>
/// time which backend the service delegates to.
///
/// <para>
/// As of C-5, SQLite is the production default. <see cref="ShouldUseSqlite"/>
/// returns <c>true</c> unconditionally except when an explicit
/// <see cref="AsyncLocal{T}"/> override is in scope; tests use that
/// override (via <c>BackendOverrideScope</c>) to opt into the
/// retained legacy LiteDB code path.
/// </para>
///
/// <para>
/// <b>Read once, not per call.</b> The factory is consulted exactly
/// once per <c>Initialize</c> call.
/// </para>
/// </summary>
internal static class DatabaseBackendFactory
{
    /// <summary>
    /// Optional per-async-flow override that pins the backend choice
    /// for the current async context. <c>null</c> means "use the
    /// production default" (SQLite). Tests use this to opt INTO the
    /// LiteDB code path when they need to exercise the legacy-backend
    /// branches still kept under the AsyncLocal override.
    /// </summary>
    private static readonly AsyncLocal<bool?> _asyncLocalOverride = new();

    /// <summary>
    /// Test hook: pins <see cref="ShouldUseSqlite"/> to a fixed value
    /// for the current async flow. Pass <c>null</c> to clear the
    /// override and fall back to the production default (SQLite).
    /// </summary>
    /// <remarks>
    /// Internal-only on purpose - production must not depend on
    /// per-async-flow backend choices. Tests reach this via
    /// <c>InternalsVisibleTo</c> on AzureBackup.Tests. Setting
    /// <c>false</c> opts the test into the legacy LiteDB code path,
    /// which is retained for migration-source-side reads.
    /// </remarks>
    internal static void SetAsyncLocalOverride(bool? value)
    {
        _asyncLocalOverride.Value = value;
    }

    /// <summary>
    /// Test hook: returns the current value of the AsyncLocal override
    /// (or <c>null</c> if unset). Used by <c>BackendOverrideScope</c> to
    /// snapshot the previous value on construction so its dispose can
    /// restore the previous value rather than blindly clearing - which
    /// matters if a future test ever nests two scopes.
    /// </summary>
    internal static bool? GetAsyncLocalOverride() => _asyncLocalOverride.Value;

    /// <summary>
    /// Returns <c>true</c> if the SQLite backend should be used.
    /// As of C-5 SQLite is the production default; this method
    /// returns <c>true</c> unconditionally except when an explicit
    /// <see cref="AsyncLocal{T}"/> test override is in scope.
    /// </summary>
    public static bool ShouldUseSqlite()
    {
        var pinned = _asyncLocalOverride.Value;
        if (pinned.HasValue) return pinned.Value;
        return true;
    }

    /// <summary>
    /// Creates and initialises a <see cref="SqliteBackend"/> against
    /// the given database path and password. Kept as a factory method
    /// so <see cref="LocalDatabaseService"/> does not need a
    /// <c>using</c> on the Backends namespace.
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
