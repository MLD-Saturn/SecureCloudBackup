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
    /// Scans <paramref name="directory"/> for the most-recent matched
    /// pair of <c>backup.db.quarantine-*</c> and
    /// <c>backup.db.salt.quarantine-*</c> files (paired by identical
    /// timestamp suffix) and returns their absolute paths. Returns
    /// <c>(null, null)</c> if the directory does not exist, contains no
    /// quarantine files, or contains quarantine files that cannot be
    /// matched into a complete pair.
    /// </summary>
    /// <remarks>
    /// The "most recent" ordering is by the parsed timestamp suffix in
    /// the filename, descending. Files whose suffix cannot be parsed are
    /// ignored. The first stamp that has BOTH a database and a salt
    /// match wins; lonely halves (database without salt or salt without
    /// database) are skipped so the caller never sees a half-populated
    /// pair.
    /// </remarks>
    /// <param name="directory">
    /// Directory to scan, typically <c>AppMode.DataDirectory</c>. Null,
    /// whitespace, or a non-existent directory returns <c>(null, null)</c>
    /// rather than throwing -- the caller is the form-open path and the
    /// user has not done anything wrong if no quarantine files exist.
    /// </param>
    /// <returns>
    /// A tuple of full paths. Either both values are non-null (a complete
    /// pair was found) or both are null (no complete pair was found).
    /// </returns>
    public static (string? DatabasePath, string? SaltPath) FindMostRecentQuarantinePair(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return (null, null);
        }

        // Two filename shapes to match. Order matters: the salt file's
        // path also contains "backup.db" so we have to test for the
        // longer ".salt.quarantine-" pattern first OR distinguish by
        // the post-stamp prefix. We do the latter so the patterns stay
        // explicit and the pair-matching logic stays simple.
        var dbStamps = new Dictionary<string, string>(StringComparer.Ordinal);
        var saltStamps = new Dictionary<string, string>(StringComparer.Ordinal);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "backup.db*.quarantine-*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var match = QuarantineStampPattern.Match(name);
            if (!match.Success) continue;

            var stamp = match.Groups[1].Value;
            // Strip the matched suffix to inspect the original artefact name.
            var prefix = name[..(name.Length - match.Length)];

            if (string.Equals(prefix, "backup.db", StringComparison.OrdinalIgnoreCase))
            {
                dbStamps[stamp] = path;
            }
            else if (string.Equals(prefix, "backup.db.salt", StringComparison.OrdinalIgnoreCase))
            {
                saltStamps[stamp] = path;
            }
            // Other prefixes (-wal, -shm, -journal companions) are ignored;
            // they do not feed the rebuild flow.
        }

        // Pair only by identical stamp. Sort the paired stamps descending
        // so the newest matched pair is returned first.
        var pairedStamps = dbStamps.Keys
            .Where(saltStamps.ContainsKey)
            .OrderByDescending(s => s, StringComparer.Ordinal)
            .ToList();

        if (pairedStamps.Count == 0)
        {
            return (null, null);
        }

        var newest = pairedStamps[0];
        return (dbStamps[newest], saltStamps[newest]);
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
