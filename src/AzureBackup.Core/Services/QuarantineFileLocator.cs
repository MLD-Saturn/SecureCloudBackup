using System.Globalization;
using System.Text.RegularExpressions;

namespace AzureBackup.Core.Services;

/// <summary>
/// B61: helper for the rebuild-from-quarantined-catalog UI flow.
///
/// <para>
/// <see cref="LocalDatabaseService.QuarantineCorruptDatabase"/> renames a
/// catalog and its sidecars with a <c>.quarantine-yyyyMMdd-HHmmss</c>
/// suffix (UTC stamp, single value per quarantine event). When the user
/// later opens the rebuild form they almost always want to point at the
/// most recent set, so we scan the data directory at form-open time and
/// pre-populate the path fields. The Browse buttons are still wired so
/// the user can re-target if the quarantined files were copied somewhere
/// else.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// Pairing is by <em>identical timestamp</em>, not by separate newest-db
/// and newest-salt -- two unrelated quarantine events would otherwise
/// produce a mismatched pair that fails the salt-sidecar size check or
/// the validate-key probe.
/// </para>
/// <para>
/// The timestamp is parsed from the filename rather than read from the
/// file system so a copy/restore that resets <c>CreationTimeUtc</c> does
/// not change the ordering. <see cref="LocalDatabaseService"/>'s rename
/// is the only producer of these names, so the pattern is authoritative.
/// </para>
/// </remarks>
public static class QuarantineFileLocator
{
    /// <summary>
    /// Matches the suffix produced by <c>QuarantineCorruptDatabase</c>:
    /// <c>.quarantine-yyyyMMdd-HHmmss</c>. The capture group holds the
    /// stamp portion so paired files can be matched by identical stamp.
    /// </summary>
    private static readonly Regex QuarantineStampPattern = new(
        @"\.quarantine-(\d{8}-\d{6})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Scans <paramref name="directory"/> for the most-recent quarantined
    /// <c>backup.db.quarantine-*</c> snapshot file and returns its absolute
    /// path, or <c>null</c> if the directory does not exist or contains no
    /// quarantined catalog.
    /// </summary>
    /// <remarks>
    /// The "most recent" ordering is by the parsed timestamp suffix in the
    /// filename, descending. Files whose suffix cannot be parsed are ignored.
    /// The application-level snapshot is a self-contained AES-256-GCM file with
    /// NO <c>.salt</c> sidecar, so only the database file matters; companion
    /// <c>-wal</c> / <c>-shm</c> / <c>-journal</c> / <c>.salt</c> quarantine
    /// files (which may exist from a legacy SQLCipher quarantine) are ignored.
    /// </remarks>
    /// <param name="directory">
    /// Directory to scan, typically <c>AppMode.DataDirectory</c>. Null,
    /// whitespace, or a non-existent directory returns <c>null</c> rather than
    /// throwing -- the caller is the form-open path and the user has not done
    /// anything wrong if no quarantine files exist.
    /// </param>
    /// <returns>
    /// The absolute path of the most-recent quarantined catalog, or <c>null</c>.
    /// </returns>
    public static string? FindMostRecentQuarantinedDatabase(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "backup.db*.quarantine-*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string? newestPath = null;
        string? newestStamp = null;

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var match = QuarantineStampPattern.Match(name);
            if (!match.Success) continue;

            // Only the main catalog file (backup.db.quarantine-...), not the
            // companion artefacts (backup.db.salt / -wal / -shm / -journal).
            var prefix = name[..(name.Length - match.Length)];
            if (!string.Equals(prefix, "backup.db", StringComparison.OrdinalIgnoreCase))
                continue;

            var stamp = match.Groups[1].Value;
            if (newestStamp is null || string.CompareOrdinal(stamp, newestStamp) > 0)
            {
                newestStamp = stamp;
                newestPath = path;
            }
        }

        return newestPath;
    }

    /// <summary>
    /// Returns true when <paramref name="filename"/> looks like a
    /// quarantine-suffixed file produced by
    /// <see cref="LocalDatabaseService.QuarantineCorruptDatabase"/>.
    /// Used by the file-picker filter so the user sees only the relevant
    /// candidates.
    /// </summary>
    public static bool IsQuarantineFileName(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return false;
        return QuarantineStampPattern.IsMatch(filename);
    }

    /// <summary>
    /// Parses the UTC timestamp embedded in a quarantine filename. Returns
    /// <c>null</c> when the suffix is missing or malformed. Exposed for
    /// tests and for any future caller that wants to display the stamp.
    /// </summary>
    public static DateTime? TryParseQuarantineStamp(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        var match = QuarantineStampPattern.Match(filename);
        if (!match.Success) return null;
        if (!DateTime.TryParseExact(
                match.Groups[1].Value,
                "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var stamp))
        {
            return null;
        }
        return stamp;
    }
}
