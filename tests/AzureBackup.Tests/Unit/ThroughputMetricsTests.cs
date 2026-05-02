using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for <see cref="ThroughputMetrics"/>.
/// </summary>
public class ThroughputMetricsTests : IDisposable
{
    private readonly string _testDir;

    public ThroughputMetricsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"metrics-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void WhenCreatedThenDirectoryIsCreated()
    {
        using var metrics = new ThroughputMetrics(_testDir);

        Assert.True(Directory.Exists(_testDir));
    }

    [Fact]
    public void WhenFileMetricsRecordedAndFlushedThenJsonlFileIsWritten()
    {
        using var metrics = new ThroughputMetrics(_testDir);

        metrics.RecordFile(new FileMetrics
        {
            Operation = "backup",
            Path = "test/file.txt",
            Bytes = 1024,
            Chunks = 1,
            ChunkMin = 1024,
            ChunkMax = 1024,
            ElapsedSeconds = 1.5,
            ThroughputMBps = 0.65,
            NewChunks = 1,
            DedupChunks = 0,
            Tier = "Hot"
        });

        metrics.RecordOperationAndFlush(new OperationMetrics
        {
            Operation = "backup",
            Files = 1,
            Succeeded = 1,
            Bytes = 1024,
            ElapsedSeconds = 1.5,
            ThroughputMBps = 0.65
        });

        var files = Directory.GetFiles(_testDir, "throughput-*.jsonl");
        Assert.Single(files);

        var lines = File.ReadAllLines(files[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"type\":\"file\"", lines[0]);
        Assert.Contains("\"type\":\"op\"", lines[1]);
    }

    [Fact]
    public void WhenFileMetricsHasRestoreFieldsThenTheyAreSerialised()
    {
        using var metrics = new ThroughputMetrics(_testDir);

        metrics.RecordFile(new FileMetrics
        {
            Operation = "restore",
            Path = "data/large.zip",
            Bytes = 268_435_456,
            Chunks = 8,
            ChunkMin = 33_554_432,
            ChunkMax = 33_554_432,
            ElapsedSeconds = 9.1,
            ThroughputMBps = 28.1,
            EffectiveConcurrency = 8,
            BudgetStalls = 3,
            Retries = 1,
            ReorderMax = 5
        });

        metrics.RecordOperationAndFlush(new OperationMetrics
        {
            Operation = "restore",
            Files = 1,
            Succeeded = 1,
            Bytes = 268_435_456,
            ElapsedSeconds = 9.1,
            ThroughputMBps = 28.1
        });

        var files = Directory.GetFiles(_testDir, "throughput-*.jsonl");
        var content = File.ReadAllText(files[0]);

        Assert.Contains("\"budget_stalls\":3", content);
        Assert.Contains("\"retries\":1", content);
        Assert.Contains("\"reorder_max\":5", content);
        Assert.Contains("\"effective_concurrency\":8", content);
    }

    [Fact]
    public void WhenDefaultFieldsThenZerosAreOmittedFromJson()
    {
        using var metrics = new ThroughputMetrics(_testDir);

        metrics.RecordOperationAndFlush(new OperationMetrics
        {
            Operation = "backup",
            Files = 5,
            Succeeded = 5,
            Bytes = 10_000,
            ElapsedSeconds = 2.0,
            ThroughputMBps = 4.77
        });

        var files = Directory.GetFiles(_testDir, "throughput-*.jsonl");
        var content = File.ReadAllText(files[0]);

        // Failed = 0 should be omitted due to WhenWritingDefault
        Assert.DoesNotContain("\"failed\"", content);
        Assert.DoesNotContain("\"retries\"", content);
    }

    [Fact]
    public void WhenDisposedThenPendingRecordsAreFlushed()
    {
        var metrics = new ThroughputMetrics(_testDir);

        metrics.RecordFile(new FileMetrics
        {
            Operation = "backup",
            Path = "orphan.txt",
            Bytes = 512
        });

        // Dispose without explicit flush
        metrics.Dispose();

        var files = Directory.GetFiles(_testDir, "throughput-*.jsonl");
        Assert.Single(files);
        Assert.Contains("orphan.txt", File.ReadAllText(files[0]));
    }

    [Fact]
    public void WhenMultipleRecordsThenAppendedToSameFile()
    {
        using var metrics = new ThroughputMetrics(_testDir);

        for (var i = 0; i < 5; i++)
        {
            metrics.RecordFile(new FileMetrics
            {
                Operation = "backup",
                Path = $"file{i}.bin",
                Bytes = i * 1000
            });
        }

        metrics.RecordOperationAndFlush(new OperationMetrics
        {
            Operation = "backup",
            Files = 5,
            Succeeded = 5
        });

        var files = Directory.GetFiles(_testDir, "throughput-*.jsonl");
        Assert.Single(files);

        var lines = File.ReadAllLines(files[0]);
        Assert.Equal(6, lines.Length); // 5 file records + 1 op record
    }

    [Fact]
    public void WhenCleanupOldFilesThenRetentionIsRespected()
    {
        using var metrics = new ThroughputMetrics(_testDir);

        // Create a fake old file
        var oldFile = Path.Combine(_testDir, "throughput-2020-01-01.jsonl");
        File.WriteAllText(oldFile, "{}\n");
        File.SetCreationTimeUtc(oldFile, DateTime.UtcNow.AddDays(-31));

        // Create a recent file
        var recentFile = Path.Combine(_testDir, "throughput-9999-12-31.jsonl");
        File.WriteAllText(recentFile, "{}\n");

        metrics.CleanupOldFiles();

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(recentFile));
    }

    /// <summary>
    /// B43 regression guard. The C# property is <c>ThroughputMBps</c> and
    /// the JSON key is pinned via <c>[JsonPropertyName]</c> to
    /// <c>throughput_mbytes_per_sec</c>. A future rename that drops the
    /// attribute would let the configured snake_case naming policy fall
    /// back to <c>throughput_m_bps</c> (or, worse, restore the historically
    /// wrong <c>throughput_mbps</c>) and silently re-introduce the
    /// MB/s-vs-Mbps label bug. This test fails noisily if either happens.
    /// </summary>
    [Fact]
    public void WhenThroughputMBpsSerialisedThenJsonKeyIsThroughputMBytesPerSec()
    {
        using var metrics = new ThroughputMetrics(_testDir);

        metrics.RecordFile(new FileMetrics
        {
            Operation = "backup",
            Path = "label-check.bin",
            Bytes = 1_048_576,
            ElapsedSeconds = 1.0,
            ThroughputMBps = 1.0
        });

        metrics.RecordOperationAndFlush(new OperationMetrics
        {
            Operation = "backup",
            Files = 1,
            Succeeded = 1,
            Bytes = 1_048_576,
            ElapsedSeconds = 1.0,
            ThroughputMBps = 1.0
        });

        var content = File.ReadAllText(Directory.GetFiles(_testDir, "throughput-*.jsonl")[0]);
        Assert.Contains("\"throughput_mbytes_per_sec\":1", content);
        Assert.DoesNotContain("\"throughput_mbps\"", content);
        Assert.DoesNotContain("\"throughput_m_bps\"", content);
    }
}
