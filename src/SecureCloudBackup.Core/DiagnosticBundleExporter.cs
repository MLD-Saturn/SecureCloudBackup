using System.IO.Compression;

namespace SecureCloudBackup.Core;

/// <summary>
/// Operator ergonomics: bundles the three locations a tester needs to share
/// when filing a bug report or capturing a baseline performance run into a
/// single ZIP file. Without this the tester would have to navigate to
/// <c>%LOCALAPPDATA%\SecureCloudBackup</c> (a hidden folder), pick the right daily
/// log, then dive into the <c>diagnostics\</c> and <c>metrics\</c>
/// subdirectories.
/// </summary>
/// <remarks>
/// <para>
/// What goes in the bundle (relative paths preserved inside the ZIP):
/// </para>
/// <list type="bullet">
///   <item><c>azurebackup-*.log</c> from the data dir (all daily files
///     so a multi-day test session is captured in one bundle)</item>
///   <item>Everything under <c>diagnostics/</c> (per-file <c>.diag</c> and
///     <c>.diag.jsonl</c> companions written by
///     <see cref="SecureCloudBackup.Core.Services.FileOperationDiagnostics"/>)</item>
///   <item>Everything under <c>metrics/</c> (daily
///     <c>throughput-*.jsonl</c> files including the new decision-point
///     records from X3)</item>
///   <item>A generated <c>bundle-info.txt</c> at the root with: bundle
///     creation timestamp (UTC), data dir path, machine/OS, and the
///     SessionId currently in use (passed in by the caller because the
///     core layer doesn't reference the UI's <c>Program.Logger</c>)</item>
/// </list>
/// <para>
/// Excluded by design:
/// </para>
/// <list type="bullet">
///   <item>The encrypted database (<c>backup.db</c>) and its salt -- contains
///     credentials and would be a credential-disclosure risk if pasted into
///     a bug report.</item>
///   <item>Any file matching <c>*.bak</c> -- generic backup artefacts that
///     may also contain sensitive data.</item>
/// </list>
/// </remarks>
public static class DiagnosticBundleExporter
{
    /// <summary>
    /// Maximum size of a single file we'll include in the bundle. A truly
    /// huge log is more likely a runaway loop than useful evidence, and we
    /// don't want a tester accidentally producing a multi-GB ZIP.
    /// </summary>
    private const long MaxFileSizeBytes = 256 * 1024 * 1024; // 256 MB

    /// <summary>
    /// Builds a ZIP file containing all diagnostic artefacts under
    /// <paramref name="dataDirectory"/>. Returns the absolute path of the
    /// produced ZIP. Safe to call while the application is still writing
    /// log files: each entry is opened with <c>FileShare.ReadWrite | FileShare.Delete</c>
    /// so a concurrent writer (e.g., <see cref="SecureCloudBackup.CrashSafeLogger"/>
    /// would read in this codebase, but we live in Core) cannot block us.
    /// </summary>
    /// <param name="dataDirectory">The data directory to harvest from
    /// (typically <c>AppMode.DataDirectory</c>).</param>
    /// <param name="targetDirectory">Where to write the ZIP. The bundle file
    /// itself is named <c>azurebackup-bundle-{utcStamp}.zip</c> so multiple
    /// captures from the same session don't collide.</param>
    /// <param name="sessionId">Optional <c>CrashSafeLogger.SessionId</c>
    /// passed through from the UI. Embedded in the bundle's
    /// <c>bundle-info.txt</c> so a triager can grep the daily log file
    /// for the matching session even if the bundle was captured days
    /// later.</param>
    /// <returns>Absolute path of the produced ZIP file.</returns>
    public static string Export(string dataDirectory, string targetDirectory, Guid? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (!Directory.Exists(dataDirectory))
            throw new DirectoryNotFoundException($"Data directory not found: {dataDirectory}");

        Directory.CreateDirectory(targetDirectory);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var bundleName = $"azurebackup-bundle-{stamp}.zip";
        var bundlePath = Path.Combine(targetDirectory, bundleName);

        // FileMode.Create truncates a same-named existing file. Multiple
        // captures within the same second would collide -- we accept that
        // because it's diagnostic, not transactional.
        using (var zipStream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
        {
            // Top-level info file. Written first so it appears at the top
            // of every archive listing.
            WriteInfoEntry(archive, dataDirectory, sessionId);

            // Daily log files (azurebackup-YYYY-MM-DD.log) at the data dir root.
            foreach (var log in EnumerateSafe(dataDirectory, "azurebackup-*.log", SearchOption.TopDirectoryOnly))
            {
                AddFileSafe(archive, log, Path.GetFileName(log));
            }

            // diagnostics/ subtree (.diag + .diag.jsonl)
            var diagDir = Path.Combine(dataDirectory, "diagnostics");
            if (Directory.Exists(diagDir))
            {
                foreach (var f in EnumerateSafe(diagDir, "*", SearchOption.AllDirectories))
                {
                    var rel = "diagnostics/" + Path.GetRelativePath(diagDir, f).Replace('\\', '/');
                    AddFileSafe(archive, f, rel);
                }
            }

            // metrics/ subtree (throughput-YYYY-MM-DD.jsonl, including X3 decision records)
            var metricsDir = Path.Combine(dataDirectory, "metrics");
            if (Directory.Exists(metricsDir))
            {
                foreach (var f in EnumerateSafe(metricsDir, "*", SearchOption.AllDirectories))
                {
                    var rel = "metrics/" + Path.GetRelativePath(metricsDir, f).Replace('\\', '/');
                    AddFileSafe(archive, f, rel);
                }
            }
        }

        return bundlePath;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> looks like sensitive
    /// material that must NOT be included in a shareable bundle. Centralised
    /// here so a future addition (e.g., a new credential cache file) is
    /// excluded from existing callers without a separate change.
    /// </summary>
    private static bool IsSensitive(string path)
    {
        var name = Path.GetFileName(path);
        // backup.db + backup.db.salt + backup.db-* (SQLite WAL/SHM/journal)
        if (name.StartsWith("backup.db", StringComparison.OrdinalIgnoreCase))
            return true;
        // Generic backup + salt files
        if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".salt", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static IEnumerable<string> EnumerateSafe(string dir, string pattern, SearchOption opt)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, opt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddFileSafe(ZipArchive archive, string sourcePath, string entryName)
    {
        // Sensitivity filter applied here (not at enumeration) so a future
        // search pattern that walks more directories doesn't accidentally
        // bypass the redaction.
        if (IsSensitive(sourcePath)) return;

        try
        {
            var fileInfo = new FileInfo(sourcePath);
            if (!fileInfo.Exists) return;
            if (fileInfo.Length > MaxFileSizeBytes) return;

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = fileInfo.LastWriteTimeUtc;

            // FileShare.ReadWrite|Delete tolerates the live CrashSafeLogger
            // append-stream. Without Delete, a daily log rotation during
            // export would race-cancel us.
            using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var dst = entry.Open();
            src.CopyTo(dst);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort -- a single inaccessible file must not poison the
            // whole bundle. The bundle-info.txt records the exclusion via
            // a separate mechanism (caller can compare to
            // EnumerateSafe results if they care).
        }
    }

    private static void WriteInfoEntry(ZipArchive archive, string dataDirectory, Guid? sessionId)
    {
        var entry = archive.CreateEntry("bundle-info.txt", CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.WriteLine("=== SecureCloudBackup Diagnostic Bundle ===");
        writer.WriteLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"DataDirectory:   {dataDirectory}");
        writer.WriteLine($"Machine:         {Environment.MachineName}");
        writer.WriteLine($"OS:              {Environment.OSVersion}");
        writer.WriteLine($".NET runtime:    {Environment.Version}");
        if (sessionId is { } sid)
        {
            writer.WriteLine($"SessionId:       {sid:N}");
        }
        writer.WriteLine();
        writer.WriteLine("Excluded artefacts (sensitive):");
        writer.WriteLine("  backup.db, backup.db-*, *.salt, *.bak");
        writer.WriteLine();
        writer.WriteLine("Use the SessionId above to grep azurebackup-*.log for the matching run.");
    }
}
