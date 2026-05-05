using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AzureBackup.Core;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for X4 <see cref="DiagnosticBundleExporter"/>: the bundle's
/// content, the sensitivity-redaction filter, and the live-file-tolerance
/// invariant (export must not block while the app is appending to a log).
/// </summary>
public class DiagnosticBundleExporterTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _outDir;

    public DiagnosticBundleExporterTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"AzbkBundle_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(root, "data");
        _outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        try
        {
            var root = Path.GetDirectoryName(_dataDir);
            if (root != null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch { /* best effort */ }
    }

    private void Seed(string relPath, string content)
    {
        var full = Path.Combine(_dataDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Export_IncludesAllExpectedArtefacts()
    {
        Seed("azurebackup-2026-04-22.log", "log line 1");
        Seed("azurebackup-2026-04-21.log", "yesterday's log");
        Seed("diagnostics/foo_Backup_20260422_120000.diag", "diag entries");
        Seed("diagnostics/foo_Backup_20260422_120000.diag.jsonl", "{\"chunk\":1}");
        Seed("metrics/throughput-2026-04-22.jsonl", "{\"type\":\"op\"}");

        var bundlePath = DiagnosticBundleExporter.Export(_dataDir, _outDir);

        Assert.True(File.Exists(bundlePath));
        Assert.StartsWith("azurebackup-bundle-", Path.GetFileName(bundlePath));
        Assert.EndsWith(".zip", bundlePath);

        using var archive = ZipFile.OpenRead(bundlePath);
        var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("bundle-info.txt", names);
        Assert.Contains("azurebackup-2026-04-22.log", names);
        Assert.Contains("azurebackup-2026-04-21.log", names);
        Assert.Contains("diagnostics/foo_Backup_20260422_120000.diag", names);
        Assert.Contains("diagnostics/foo_Backup_20260422_120000.diag.jsonl", names);
        Assert.Contains("metrics/throughput-2026-04-22.jsonl", names);
    }

    [Fact]
    public void Export_RedactsSensitiveArtefacts()
    {
        // Every kind of sensitive artefact the redaction filter is supposed
        // to catch. A regression that drops one of these from the filter
        // becomes a credential-disclosure bug, so the assertions are strict.
        Seed("backup.db", "encrypted database");
        Seed("backup.db.salt", "argon2 salt");
        Seed("backup.db-wal", "sqlite WAL");
        Seed("backup.db-shm", "sqlite SHM");
        Seed("backup.db.bak", "generic backup");
        // One innocent file to confirm the filter is not over-broad.
        Seed("azurebackup-2026-04-22.log", "should be included");

        var bundlePath = DiagnosticBundleExporter.Export(_dataDir, _outDir);

        using var archive = ZipFile.OpenRead(bundlePath);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.DoesNotContain(names, n => n.Contains("backup.db", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.EndsWith(".salt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("azurebackup-2026-04-22.log", names);
    }

    [Fact]
    public void Export_BundleInfo_ContainsSessionIdWhenProvided()
    {
        // SessionId is the correlation key between the bundle and the
        // running process's log lines. Must be present and discoverable.
        var sid = Guid.NewGuid();
        Seed("azurebackup-2026-04-22.log", "x");

        var bundlePath = DiagnosticBundleExporter.Export(_dataDir, _outDir, sid);

        using var archive = ZipFile.OpenRead(bundlePath);
        var infoEntry = archive.GetEntry("bundle-info.txt");
        Assert.NotNull(infoEntry);
        using var reader = new StreamReader(infoEntry!.Open());
        var text = reader.ReadToEnd();
        Assert.Contains(sid.ToString("N"), text);
        Assert.Contains("AzureBackup Diagnostic Bundle", text);
        Assert.Contains(_dataDir, text);
    }

    [Fact]
    public void Export_BundleInfo_OmitsSessionIdLineWhenNotProvided()
    {
        Seed("azurebackup-2026-04-22.log", "x");
        var bundlePath = DiagnosticBundleExporter.Export(_dataDir, _outDir, sessionId: null);

        using var archive = ZipFile.OpenRead(bundlePath);
        var infoEntry = archive.GetEntry("bundle-info.txt");
        using var reader = new StreamReader(infoEntry!.Open());
        var text = reader.ReadToEnd();
        Assert.DoesNotContain("SessionId:", text);
    }

    [Fact]
    public void Export_Tolerates_FileOpenForWriting()
    {
        // Critical invariant for live capture: the exporter must not block
        // because the app is currently appending to a log file. Pre-X4 a
        // naive File.OpenRead with FileShare.None would throw IOException
        // for the daily azurebackup-*.log file -- making the bundle command
        // fail right when you most need it.
        var logPath = Path.Combine(_dataDir, "azurebackup-2026-04-22.log");
        using var openLog = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        openLog.Write("live writer\n"u8);
        openLog.Flush();

        var bundlePath = DiagnosticBundleExporter.Export(_dataDir, _outDir);

        using var archive = ZipFile.OpenRead(bundlePath);
        Assert.NotNull(archive.GetEntry("azurebackup-2026-04-22.log"));
    }

    [Fact]
    public void Export_MissingDataDirectory_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            DiagnosticBundleExporter.Export(Path.Combine(_dataDir, "does-not-exist"), _outDir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Export_NullOrWhitespaceDataDir_Throws(string? badDir)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            DiagnosticBundleExporter.Export(badDir!, _outDir));
    }

    [Fact]
    public void Export_EmptyDataDir_StillProducesBundleWithInfoFile()
    {
        // No log files yet, no diagnostics dir, no metrics dir. The export
        // should still produce a valid ZIP containing only bundle-info.txt
        // (so the user never gets a "no bundle was created" error message
        // -- they just get a small bundle that proves the action ran).
        var bundlePath = DiagnosticBundleExporter.Export(_dataDir, _outDir);

        using var archive = ZipFile.OpenRead(bundlePath);
        Assert.Single(archive.Entries);
        Assert.Equal("bundle-info.txt", archive.Entries[0].FullName);
    }
}
