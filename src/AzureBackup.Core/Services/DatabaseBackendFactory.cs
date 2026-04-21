using AzureBackup.Core.Services.Backends;

namespace AzureBackup.Core.Services;

/// <summary>
/// Option C / C-1 final step b: decides at
/// <see cref="LocalDatabaseService.Initialize(string, ReadOnlySpan{char})"/>
/// time which backend the service delegates to.
///
/// <para>
/// The flag is the environment variable <c>AZBK_USE_SQLITE</c>. Values
/// <c>1</c>, <c>true</c>, <c>yes</c>, and <c>on</c> (case-insensitive,
/// trimmed) enable the SQLite backend. Any other value - including
/// unset, empty, <c>0</c>, <c>false</c> - leaves the service on its
/// original LiteDB code path.
/// </para>
///
/// <para>
/// <b>Default-off is the safe choice.</b> All existing users and all
/// existing integration tests see the LiteDB path by default. A single
/// env-var flip routes them to SQLite with zero other changes. This is
/// the preview-flag gate the eval doc \u00a711.8 prescribes for the C-6
/// soak phase.
/// </para>
///
/// <para>
/// <b>Read once, not per call.</b> The factory is consulted exactly
/// once per <c>Initialize</c> call; flipping the env var mid-session
/// is undefined. Production code never flips it, and tests that need
/// to exercise both paths should run in separate processes (xunit
/// creates separate AppDomains per test class which is sufficient).
/// </para>
/// </summary>
internal static class DatabaseBackendFactory
{
    /// <summary>
    /// Optional per-async-flow override that pins the backend choice
    /// for the current async context. <c>null</c> means "use the
    /// production default" (SQLite). Tests use this to opt INTO the
    /// LiteDB code path when they need to seed data in the legacy
    /// format - e.g. the migration integration tests that need a
    /// LiteDB DB on disk before exercising MigrateFromLiteDb.
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
    public static SqliteBackend CreateAndInitializeSqlite(
        string databasePath, ReadOnlySpan<char> password)
    {
        var backend = new SqliteBackend();
        backend.Initialize(databasePath, password);
        return backend;
    }
}
