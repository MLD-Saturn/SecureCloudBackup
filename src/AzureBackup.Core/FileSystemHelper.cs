using System.Security.Cryptography;

namespace AzureBackup.Core;

/// <summary>
/// Shared filesystem utilities.
/// </summary>
public static class FileSystemHelper
{
    /// <summary>
    /// Recursively removes empty directories under the given path.
    /// Errors are silently ignored (best-effort cleanup).
    /// </summary>
    public static void CleanEmptyDirectories(string directory)
    {
        foreach (var subDir in Directory.EnumerateDirectories(directory))
        {
            CleanEmptyDirectories(subDir);

            if (!Directory.EnumerateFileSystemEntries(subDir).Any())
            {
                try
                {
                    Directory.Delete(subDir);
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
    }

    /// <summary>
    /// Best-effort delete of a file. Returns <c>true</c> when the file is gone
    /// after the call (either we deleted it, or it was already missing),
    /// <c>false</c> on a logged failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Centralises the
    /// <c>try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }</c>
    /// pattern that previously appeared inline at ~25 sites. Use this for
    /// every plain <see cref="File.Delete(string)"/> in production code so
    /// the cleanup contract is uniform: missing files are not an error,
    /// expected I/O exceptions are swallowed and reported via
    /// <paramref name="log"/>, and unexpected exception types still bubble.
    /// </para>
    /// <para>
    /// Use <see cref="TrySecureDelete"/> instead for files that contain
    /// secret key material (database, salt, encryption-config artefacts).
    /// </para>
    /// </remarks>
    /// <param name="path">Absolute or relative path of the file to delete.
    /// May be <c>null</c> or empty (treated as a no-op for ergonomics).</param>
    /// <param name="log">Optional sink for the failure message; receives a
    /// single line on a caught exception. Pass <c>null</c> to suppress.</param>
    public static bool TryDelete(string? path, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(path)) return true;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                    or UnauthorizedAccessException
                                    or DirectoryNotFoundException
                                    or PathTooLongException
                                    or NotSupportedException)
        {
            log?.Invoke($"TryDelete: failed to delete '{path}' — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Best-effort secure delete of a file: overwrites the contents with one
    /// pass of cryptographically random bytes, flushes the OS write buffer
    /// to disk, then unlinks the file. Returns <c>true</c> when the file is
    /// gone after the call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Threat model — please read before adding new callers:
    /// </para>
    /// <list type="bullet">
    ///   <item>Designed for files containing key material (database, Argon2id
    ///     salt, legacy encrypted config) on hosts where the underlying
    ///     storage may not be encrypted at rest. Use <see cref="TryDelete"/>
    ///     for everything else.</item>
    ///   <item>Single random pass + <see cref="FileStream.Flush(bool)"/>
    ///     follows NIST SP 800-88r1 guidance for "clear" on rotational
    ///     media. The previous helper did 3 passes, which is cargo-culted
    ///     from the 1996 Gutmann paper and provides no additional security
    ///     on modern drives.</item>
    ///   <item>This is a <b>best-effort</b> defence. On SSDs, copy-on-write
    ///     filesystems (APFS / Btrfs / ReFS), and full-disk-encrypted volumes
    ///     (BitLocker / FileVault / LUKS) the overwrite typically lands on
    ///     fresh blocks while the original sectors remain readable until
    ///     firmware-level garbage collection. The win is on legacy
    ///     filesystems and removable media.</item>
    ///   <item>Falls back to a plain <see cref="TryDelete"/> if the
    ///     overwrite step fails (file open by another process, read-only,
    ///     etc.). Callers get the unlink either way.</item>
    /// </list>
    /// </remarks>
    public static bool TrySecureDelete(string? path, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(path)) return true;
        if (!File.Exists(path)) return true;

        try
        {
            var fileSize = new FileInfo(path).Length;
            if (fileSize > 0)
            {
                // FileShare.Read instead of None: tolerate concurrent
                // read-only handles (some AV scanners hold the file open
                // briefly after our own close). FileShare.None would force
                // an IOException → fallback path → no overwrite at all.
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Write, FileShare.Read);

                var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    var remaining = fileSize;
                    while (remaining > 0)
                    {
                        var toWrite = (int)Math.Min(rented.Length, remaining);
                        RandomNumberGenerator.Fill(rented.AsSpan(0, toWrite));
                        stream.Write(rented, 0, toWrite);
                        remaining -= toWrite;
                    }
                    // Flush(true) forces the OS to push the dirty buffers to
                    // the underlying device before we unlink. Without this,
                    // the random bytes can sit in the page cache and never
                    // hit disk — defeating the whole point of overwriting.
                    stream.Flush(flushToDisk: true);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                }
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                    or UnauthorizedAccessException
                                    or DirectoryNotFoundException
                                    or PathTooLongException
                                    or NotSupportedException)
        {
            log?.Invoke($"TrySecureDelete: overwrite failed for '{path}' — " +
                        $"{ex.GetType().Name}: {ex.Message}. Falling back to plain delete.");
            return TryDelete(path, log);
        }
    }
}
