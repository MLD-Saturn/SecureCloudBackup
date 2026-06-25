namespace SecureCloudBackup.Core.Services.Backends;

/// <summary>
/// Single source of truth for the on-disk file-name conventions of a catalog
/// (currently just the <c>.salt</c> sidecar). Centralised here so the salt-path
/// rule is defined exactly once instead of being re-spelled as
/// <c>path + ".salt"</c> at every call site.
/// </summary>
internal static class CatalogPaths
{
    /// <summary>The suffix of the salt sidecar that lives next to the database file.</summary>
    internal const string SaltSuffix = ".salt";

    /// <summary>Returns the path of the salt sidecar for <paramref name="databasePath"/>.</summary>
    internal static string GetSaltFilePath(string databasePath) => databasePath + SaltSuffix;
}
