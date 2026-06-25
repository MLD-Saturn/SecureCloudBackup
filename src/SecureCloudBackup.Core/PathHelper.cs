namespace SecureCloudBackup.Core;

/// <summary>
/// Shared path manipulation utilities.
/// </summary>
public static class PathHelper
{
    /// <summary>
    /// Comparison used for all containment / prefix checks. Ordinal-ignore-case
    /// matches the rest of this file's conventions and is the conservative
    /// choice for the security containment checks: on the restore hot path the
    /// target is always built as <c>Path.Combine(root, relative)</c> so the
    /// root prefix is byte-identical to the root by construction, and a
    /// traversal attempt (<c>..</c>) normalizes to a path that does not share
    /// the root prefix regardless of case.
    /// </summary>
    private const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="candidatePath"/> is
    /// <paramref name="directory"/> itself or a descendant of it, after both are
    /// normalized with <see cref="Path.GetFullPath(string)"/> (which resolves any
    /// <c>..</c> / <c>.</c> segments).
    /// <para>
    /// The check appends a directory separator to <paramref name="directory"/>
    /// before the prefix comparison so a sibling such as <c>C:\WindowsApps</c> is
    /// NOT considered within <c>C:\Windows</c>. A bare
    /// <c>fullPath.StartsWith(dir)</c> (without the separator) has that boundary
    /// bug and must not be used for security-relevant containment.
    /// </para>
    /// </summary>
    /// <param name="candidatePath">The path to test. Relative paths are resolved against the current directory.</param>
    /// <param name="directory">The directory that should contain (or equal) the candidate.</param>
    public static bool IsWithinDirectory(string candidatePath, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var normalizedCandidate = Path.GetFullPath(candidatePath);
        var normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // The candidate IS the directory.
        if (string.Equals(normalizedCandidate, normalizedDirectory, PathComparison))
            return true;

        // The candidate is a strict descendant: it must start with the directory
        // followed by a separator so "C:\WindowsApps" is not within "C:\Windows".
        var directoryWithSeparator = normalizedDirectory + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(directoryWithSeparator, PathComparison);
    }

    /// <summary>
    /// Gets the relative path from a known base path.
    /// Normalizes both paths and falls back to the filename if the path is not under the base.
    /// </summary>
    public static string GetRelativePathFromBase(string fullPath, string basePath)
    {
        fullPath = Path.GetFullPath(fullPath);
        basePath = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath[basePath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(relative) ? Path.GetFileName(fullPath) : relative;
        }

        return Path.GetFileName(fullPath);
    }

    /// <summary>
    /// Finds the longest common root directory among a list of paths.
    /// Returns empty string if no common root exists.
    /// </summary>
    public static string FindCommonRoot(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return string.Empty;

        var directories = paths
            .Select(p => Path.GetDirectoryName(p) ?? string.Empty)
            .Where(d => !string.IsNullOrEmpty(d))
            .ToList();

        if (directories.Count == 0)
            return string.Empty;

        var commonRoot = directories[0];
        foreach (var dir in directories.Skip(1))
        {
            while (!dir.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase) && commonRoot.Length > 0)
            {
                var parentDir = Path.GetDirectoryName(commonRoot);
                if (string.IsNullOrEmpty(parentDir) || parentDir == commonRoot)
                {
                    commonRoot = string.Empty;
                    break;
                }
                commonRoot = parentDir;
            }
        }

        return commonRoot;
    }

    /// <summary>
    /// Gets a display name for a path, handling drive roots (e.g. "J:\") where
    /// Path.GetFileName returns empty string.
    /// </summary>
    public static string GetDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);

        if (string.IsNullOrEmpty(name))
            return trimmed + Path.DirectorySeparatorChar;

        return name;
    }
}
