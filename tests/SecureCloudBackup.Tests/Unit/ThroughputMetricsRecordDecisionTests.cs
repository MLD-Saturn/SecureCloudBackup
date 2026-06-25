using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SecureCloudBackup.Core;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Tests for X3 RecordDecision. The wired call sites in BackupOrchestrator
/// and RestoreService are exercised by integration tests against the live
/// Azure SDK; these unit tests validate the record contract: format,
/// snake_case JSONL output, immediate-flush semantics, and null-context
/// tolerance.
/// </summary>
public class ThroughputMetricsRecordDecisionTests : IDisposable
{
    private readonly string _tempDir;

    public ThroughputMetricsRecordDecisionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AzbkDecision_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private string TodayJsonl() =>
        Path.Combine(_tempDir, $"throughput-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

    // ThroughputMetrics keeps a long-lived append writer open while the sink
    // is alive, so a reader must opt into FileShare.ReadWrite (the default
    // File.ReadAllLines requests FileShare.Read and throws IOException against
    // the open write handle). External log-tailing tooling reads the same way.
    private static string[] ReadAllSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void RecordDecision_WritesImmediately_ToDailyJsonl()
    {
        // Distinguishes RecordDecision from RecordFile: file records sit in
        // the queue until RecordOperationAndFlush. Decision records flush
        // on every call so a hard kill mid-op preserves the justification.
        using var metrics = new ThroughputMetrics(_tempDir);

        metrics.RecordDecision("backup-concurrency", new Dictionary<string, object?>
        {
            ["files"] = 100,
            ["maxParallelFileBackups"] = 4
        });

        var jsonlPath = TodayJsonl();
        Assert.True(File.Exists(jsonlPath), "Decision record must be flushed to disk immediately");
        var line = ReadAllSharedLines(jsonlPath).Single();
        Assert.Contains("\"type\":\"decision\"", line);
        Assert.Contains("\"reason\":\"backup-concurrency\"", line);
        Assert.Contains("\"files\":\"100\"", line);
        Assert.Contains("\"max_parallel_file_backups\":\"4\"", line);
    }

    [Fact]
    public void RecordDecision_WithNullContext_StillEmits()
    {
        // A bare reason without context is valid (e.g., "fell back to legacy
        // path"). The record must still produce a JSON line.
        using var metrics = new ThroughputMetrics(_tempDir);

        metrics.RecordDecision("legacy-fallback");

        var line = ReadAllSharedLines(TodayJsonl()).Single();
        Assert.Contains("\"type\":\"decision\"", line);
        Assert.Contains("\"reason\":\"legacy-fallback\"", line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void RecordDecision_NullOrWhitespaceReason_Throws(string? reason)
    {
        // ThrowsAny because ArgumentNullException is a subclass of
        // ArgumentException; the BCL guard helper picks one or the other
        // depending on the input, and we accept either.
        using var metrics = new ThroughputMetrics(_tempDir);
        Assert.ThrowsAny<ArgumentException>(() => metrics.RecordDecision(reason!));
    }

    [Fact]
    public void RecordDecision_StringifiesNonStringContext()
    {
        // Context dictionary values are object?: they get stringified via
        // ToString() so the final JSONL is a flat key/value bag (greppable).
        using var metrics = new ThroughputMetrics(_tempDir);

        metrics.RecordDecision("memory-budget-clamp", new Dictionary<string, object?>
        {
            ["stallCount"] = 7L,
            ["budgetMb"] = 256L,
            ["unlimited"] = false,
            ["nullField"] = null
        });

        var line = ReadAllSharedLines(TodayJsonl()).Single();
        Assert.Contains("\"stall_count\":\"7\"", line);
        Assert.Contains("\"budget_mb\":\"256\"", line);
        Assert.Contains("\"unlimited\":\"False\"", line);
        Assert.Contains("\"null_field\":\"null\"", line);
    }

    [Fact]
    public void RecordDecision_MultipleCalls_Append()
    {
        // Each decision is a separate JSONL line. Verifies the per-call
        // flush does not truncate the file.
        using var metrics = new ThroughputMetrics(_tempDir);

        metrics.RecordDecision("decision-1");
        metrics.RecordDecision("decision-2");
        metrics.RecordDecision("decision-3");

        var lines = ReadAllSharedLines(TodayJsonl());
        Assert.Equal(3, lines.Length);
        Assert.Contains("decision-1", lines[0]);
        Assert.Contains("decision-2", lines[1]);
        Assert.Contains("decision-3", lines[2]);
    }
}
