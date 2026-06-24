using SQLitePCL;

namespace AzureBackup.Migration;

/// <summary>
/// Registers the SQLCipher SQLite provider exactly once per process. This helper
/// process references ONLY the SQLCipher bundle, but we register explicitly (and
/// idempotently) so the active engine is deterministic regardless of
/// module-initializer ordering. Shared by <see cref="LegacyCatalogMigrator"/>
/// (reads a legacy catalog) and <see cref="SeedRunner"/> (writes a test catalog).
/// </summary>
internal static class SqlCipherProvider
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>Registers <c>SQLite3Provider_e_sqlcipher</c> as the SQLite provider once.</summary>
    public static void Ensure()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            raw.SetProvider(new SQLite3Provider_e_sqlcipher());
            _registered = true;
        }
    }
}
