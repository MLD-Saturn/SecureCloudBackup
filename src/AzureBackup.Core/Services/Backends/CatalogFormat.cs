using AzureBackup.Crypto;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// Sniffs the on-disk format of a catalog database file so the
/// <see cref="DatabaseBackendFactory"/> can route to the right backend (the new
/// in-memory snapshot vs. a legacy SQLCipher database that must be migrated
/// first) WITHOUT attempting to decrypt anything.
/// </summary>
internal static class CatalogFormat
{
    /// <summary>The format of a catalog file on disk.</summary>
    internal enum Kind
    {
        /// <summary>No usable database file exists at the path.</summary>
        Missing,

        /// <summary>The new application-level encrypted snapshot (AZDB magic header).</summary>
        Snapshot,

        /// <summary>A legacy SQLCipher-encrypted database (a non-AZDB file with a salt sidecar).</summary>
        LegacySqlCipher,
    }

    /// <summary>
    /// Determines the <see cref="Kind"/> of the file at <paramref name="databasePath"/>.
    /// A file beginning with the AZDB magic is the new snapshot; any other
    /// non-empty file accompanied by a <c>.salt</c> sidecar is treated as a legacy
    /// SQLCipher database (its contents are encrypted, so the salt sidecar plus a
    /// non-AZDB header is the strongest non-decrypting signal available).
    /// </summary>
    public static Kind Detect(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return Kind.Missing;

        long length;
        try
        {
            length = new FileInfo(databasePath).Length;
        }
        catch
        {
            return Kind.Missing;
        }
        if (length == 0)
            return Kind.Missing;

        // Read just enough bytes to test for the AZDB magic header.
        var header = new byte[4];
        try
        {
            using var fs = File.OpenRead(databasePath);
            var read = fs.Read(header, 0, header.Length);
            if (read >= 4 && DbSnapshotEnvelope.HasMagic(header))
                return Kind.Snapshot;
        }
        catch
        {
            return Kind.Missing;
        }

        // Not the new snapshot. A legacy SQLCipher catalog is encrypted (so its
        // header is opaque) and always has a salt sidecar next to it.
        var saltPath = CatalogPaths.GetSaltFilePath(databasePath);
        return File.Exists(saltPath) ? Kind.LegacySqlCipher : Kind.Missing;
    }
}
