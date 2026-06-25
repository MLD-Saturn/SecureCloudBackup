namespace SecureCloudBackup.Crypto;

/// <summary>
/// Small file helpers shared across the solution. Lives in the engine-agnostic
/// crypto library because it has no dependencies beyond the BCL and is needed by
/// every project that persists encrypted blobs (the snapshot backend and the
/// migration helper).
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> via a
    /// write-temp-then-atomic-rename sequence: the bytes are written to a temp
    /// file in the same directory and flushed to disk, then the temp is
    /// atomically renamed over the destination.
    /// </summary>
    /// <remarks>
    /// Crash-safety guarantee: because the destination is replaced by a single
    /// atomic rename (<c>MoveFileEx</c> / <c>rename</c>) performed only AFTER the
    /// new content is fully written and flushed, a crash or failure at any point
    /// BEFORE the rename leaves the previous complete file intact, and the rename
    /// transitions the destination from the old complete file to the new complete
    /// file with no observable partial state. (A failure injected AT the rename
    /// step itself is platform-specific and not part of this guarantee -- the OS
    /// rename is the atomic commit point.) Creates the destination directory if
    /// it does not already exist.
    /// </remarks>
    public static void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            // Atomic replace: MoveFileEx(MOVEFILE_REPLACE_EXISTING) on Windows,
            // rename(2) on Unix -- both atomic on the same volume.
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }
}
