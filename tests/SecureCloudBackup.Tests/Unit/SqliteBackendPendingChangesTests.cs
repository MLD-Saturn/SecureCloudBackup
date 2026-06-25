using SecureCloudBackup.Core.Models;
using SecureCloudBackup.Core.Services.Backends;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Option C / C-1f: round-trip tests for the pending-changes queue
/// (<c>pending_changes</c> table) on the SQLite backend.
///
/// <para>
/// Critical contract under test: <c>QueueFileChange</c> and
/// <c>QueueFileChangesBatch</c> both replace any existing pending row
/// for the same FilePath ("last write wins"), and the bulk variant
/// performs the entire DELETE+INSERT under one transaction so a reader
/// never observes a divergent state.
/// </para>
///
/// <para>
/// CleanupStalePendingChanges is intentionally NOT in IDatabaseBackend
/// because it interleaves persistence with file-system I/O; that
/// orchestration belongs in LocalDatabaseService once it routes through
/// the backend (later phase). It will use the primitives covered here
/// (GetPendingChanges + RemovePendingChange).
/// </para>
/// </summary>
public class SqliteBackendPendingChangesTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly SqliteBackend _backend;

    public SqliteBackendPendingChangesTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "pending.db");
        _backend = new InMemorySnapshotBackend();
        _backend.Initialize(_dbPath, "PendingTestPwd!".AsSpan());
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    private static FileChangeEvent MakeChange(string path, FileChangeType type = FileChangeType.Modified,
        DateTime? detectedAt = null) => new()
        {
            FilePath = path,
            ChangeType = type,
            DetectedAt = detectedAt ?? new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc),
        };

    [Fact]
    public void GetPendingChanges_FreshDb_ReturnsEmpty()
    {
        Assert.Empty(_backend.GetPendingChanges());
    }

    [Fact]
    public void QueueFileChange_RoundTripsAllFields()
    {
        var change = MakeChange(@"C:\src\a.txt", FileChangeType.Created,
            new DateTime(2026, 4, 17, 22, 0, 0, DateTimeKind.Utc));
        _backend.QueueFileChange(change);

        var pending = _backend.GetPendingChanges();
        var row = Assert.Single(pending);
        Assert.Equal(@"C:\src\a.txt", row.FilePath);
        Assert.Equal(FileChangeType.Created, row.ChangeType);
        Assert.Equal(change.DetectedAt, row.DetectedAt);
        Assert.Equal(DateTimeKind.Utc, row.DetectedAt.Kind);
    }

    [Fact]
    public void QueueFileChange_SamePath_ReplacesExisting()
    {
        // Arrange: queue Created event, then Modified for the same path.
        _backend.QueueFileChange(MakeChange(@"C:\swap.txt", FileChangeType.Created));
        _backend.QueueFileChange(MakeChange(@"C:\swap.txt", FileChangeType.Modified));

        // Assert: one row remains, and it's the latest one.
        var pending = _backend.GetPendingChanges();
        var row = Assert.Single(pending);
        Assert.Equal(FileChangeType.Modified, row.ChangeType);
    }

    [Fact]
    public void QueueFileChangesBatch_DeduplicatesByPath_LastWriteWins()
    {
        // Arrange: input has two events for the same path; the second wins.
        var first = MakeChange(@"C:\dup.txt", FileChangeType.Created);
        var second = MakeChange(@"C:\dup.txt", FileChangeType.Modified,
            first.DetectedAt.AddSeconds(1));

        _backend.QueueFileChangesBatch(new[] { first, second });

        var pending = _backend.GetPendingChanges();
        var row = Assert.Single(pending);
        Assert.Equal(FileChangeType.Modified, row.ChangeType);
    }

    [Fact]
    public void QueueFileChangesBatch_DeduplicatesCaseInsensitive()
    {
        // Match the LiteDB-era OrdinalIgnoreCase contract.
        var lower = MakeChange(@"C:\case\file.txt", FileChangeType.Created);
        var upper = MakeChange(@"C:\CASE\FILE.TXT", FileChangeType.Modified,
            lower.DetectedAt.AddSeconds(1));

        _backend.QueueFileChangesBatch(new[] { lower, upper });

        var pending = _backend.GetPendingChanges();
        var row = Assert.Single(pending);
        Assert.Equal(FileChangeType.Modified, row.ChangeType);
    }

    [Fact]
    public void QueueFileChangesBatch_OverwritesExistingPendingRows()
    {
        // Arrange: prior pending row.
        _backend.QueueFileChange(MakeChange(@"C:\overwrite.txt", FileChangeType.Created));

        // Act: batch contains a fresh event for the same path plus a new one.
        _backend.QueueFileChangesBatch(new[]
        {
            MakeChange(@"C:\overwrite.txt", FileChangeType.Modified),
            MakeChange(@"C:\new.txt", FileChangeType.Created),
        });

        // Assert: 2 rows, the existing-path row was replaced.
        var pending = _backend.GetPendingChanges();
        Assert.Equal(2, pending.Count);
        var overwrite = Assert.Single(pending, p => p.FilePath == @"C:\overwrite.txt");
        Assert.Equal(FileChangeType.Modified, overwrite.ChangeType);
    }

    [Fact]
    public void QueueFileChangesBatch_EmptyOrAllNullEntries_NoOp()
    {
        _backend.QueueFileChangesBatch(Array.Empty<FileChangeEvent>());
        Assert.Empty(_backend.GetPendingChanges());

        _backend.QueueFileChangesBatch(new FileChangeEvent[] { null!, null! });
        Assert.Empty(_backend.GetPendingChanges());
    }

    [Fact]
    public void GetPendingChanges_OrdersByDetectedAtAscending()
    {
        // Arrange: insert out of chronological order.
        var t0 = new DateTime(2026, 4, 17, 20, 0, 0, DateTimeKind.Utc);
        _backend.QueueFileChange(MakeChange(@"C:\third.txt",  detectedAt: t0.AddMinutes(2)));
        _backend.QueueFileChange(MakeChange(@"C:\first.txt",  detectedAt: t0));
        _backend.QueueFileChange(MakeChange(@"C:\second.txt", detectedAt: t0.AddMinutes(1)));

        // Act
        var pending = _backend.GetPendingChanges();

        // Assert
        Assert.Equal(3, pending.Count);
        Assert.Equal(@"C:\first.txt",  pending[0].FilePath);
        Assert.Equal(@"C:\second.txt", pending[1].FilePath);
        Assert.Equal(@"C:\third.txt",  pending[2].FilePath);
    }

    [Fact]
    public void GetPendingChanges_RespectsBatchSize()
    {
        for (var i = 0; i < 25; i++)
        {
            _backend.QueueFileChange(MakeChange($@"C:\batch\f{i:D3}.txt",
                detectedAt: new DateTime(2026, 4, 17, 0, 0, i, DateTimeKind.Utc)));
        }

        var firstTen = _backend.GetPendingChanges(batchSize: 10);

        Assert.Equal(10, firstTen.Count);
        // Earliest 10 by DetectedAt -> f000..f009
        Assert.Equal(@"C:\batch\f000.txt", firstTen[0].FilePath);
        Assert.Equal(@"C:\batch\f009.txt", firstTen[^1].FilePath);
    }

    [Fact]
    public void GetPendingChanges_NonPositiveBatchSize_FallsBackToDefault()
    {
        for (var i = 0; i < 3; i++)
        {
            _backend.QueueFileChange(MakeChange($@"C:\f{i}.txt"));
        }

        Assert.Equal(3, _backend.GetPendingChanges(batchSize: 0).Count);
        Assert.Equal(3, _backend.GetPendingChanges(batchSize: -100).Count);
    }

    [Fact]
    public void RemovePendingChange_ExistingPath_RemovesAllRowsForPath()
    {
        _backend.QueueFileChange(MakeChange(@"C:\rm.txt"));
        _backend.QueueFileChange(MakeChange(@"C:\keep.txt"));

        _backend.RemovePendingChange(@"C:\rm.txt");

        var remaining = _backend.GetPendingChanges();
        Assert.Single(remaining);
        Assert.Equal(@"C:\keep.txt", remaining[0].FilePath);
    }

    [Fact]
    public void RemovePendingChange_MissingPath_NoOp()
    {
        _backend.RemovePendingChange(@"C:\not-there.txt");
        // Just shouldn't throw - matches LiteDB DeleteMany contract.
    }

    [Fact]
    public void GetAllPendingChangePaths_ReturnsCaseInsensitiveSet()
    {
        _backend.QueueFileChange(MakeChange(@"C:\one.txt"));
        _backend.QueueFileChange(MakeChange(@"C:\TWO.txt"));

        var paths = _backend.GetAllPendingChangePaths();

        Assert.Equal(2, paths.Count);
        // The set must be case-insensitive so callers can answer "is this
        // path pending?" without manually normalising casing first.
        Assert.Contains(@"c:\one.TXT", paths);
        Assert.Contains(@"C:\two.TXT", paths);
    }

    [Fact]
    public void QueueFileChange_SurvivesReopen()
    {
        _backend.QueueFileChange(MakeChange(@"C:\persist.txt", FileChangeType.Deleted));
        _backend.Dispose();

        using var reopened = new InMemorySnapshotBackend();
        reopened.Initialize(_dbPath, "PendingTestPwd!".AsSpan());

        var pending = reopened.GetPendingChanges();
        var row = Assert.Single(pending);
        Assert.Equal(@"C:\persist.txt", row.FilePath);
        Assert.Equal(FileChangeType.Deleted, row.ChangeType);
    }

    [Fact]
    public void QueueFileChange_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _backend.QueueFileChange(null!));
    }

    [Fact]
    public void QueueFileChangesBatch_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _backend.QueueFileChangesBatch(null!));
    }

    [Fact]
    public void RemovePendingChange_NullOrWhitespace_Throws()
    {
        Assert.Throws<ArgumentException>(() => _backend.RemovePendingChange(""));
        Assert.Throws<ArgumentException>(() => _backend.RemovePendingChange("   "));
    }
}
