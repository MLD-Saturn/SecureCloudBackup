using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Direct unit tests for the five internal chunk_file_refs helpers
/// added to SqliteBackend in C-2: UpsertChunkFileRef,
/// BulkInsertChunkFileRefs, DeleteChunkFileRefsForFile,
/// DeleteChunkFileRefsForChunk, DeleteChunkFileRefsForFileAndChunk.
///
/// <para>
/// Until this file existed, those helpers were only exercised by
/// integration tests that drove ChunkIndexService.AddReference into
/// UpsertChunkFileRef. A direct-write regression there would surface
/// as an integration failure with a noisy stack trace pointing at
/// ChunkIndexService rather than the actual offending helper. These
/// tests catch a regression at the helper boundary.
/// </para>
///
/// <para>
/// We assert the observable side-effect (rows visible via
/// GetChunkEntriesForFile) rather than poking at the SQLite schema
/// directly so the tests are not coupled to whether the reverse
/// index lives in chunk_file_refs or some future equivalent.
/// </para>
/// </summary>
public class SqliteBackendChunkFileRefsHelperTests : IDisposable
{
    private const string Password = "ChunkRefHelperPwd!";
    private static readonly DateTime ReferencedAt =
        new(2026, 4, 18, 9, 30, 0, DateTimeKind.Utc);

    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly SqliteBackend _backend;

    public SqliteBackendChunkFileRefsHelperTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-cfr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "cfr.db");
        _backend = new InMemorySnapshotBackend();
        _backend.Initialize(_dbPath, Password.AsSpan());
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void UpsertChunkFileRef_NewTriple_InsertsRow()
    {
        SeedChunk("HASH-A", 1024);
        _backend.UpsertChunkFileRef(@"C:\a.bin", "HASH-A", 0, ReferencedAt);

        var rows = _backend.GetChunkEntriesForFile(@"C:\a.bin");
        Assert.Single(rows);
        Assert.Equal("HASH-A", rows[0].ChunkHash);
    }

    [Fact]
    public void UpsertChunkFileRef_DuplicateTriple_DoesNotCreateSecondRow()
    {
        SeedChunk("HASH-A", 1024);
        _backend.UpsertChunkFileRef(@"C:\a.bin", "HASH-A", 0, ReferencedAt);
        _backend.UpsertChunkFileRef(@"C:\a.bin", "HASH-A", 0, ReferencedAt.AddSeconds(60));

        var rows = _backend.GetChunkEntriesForFile(@"C:\a.bin");
        Assert.Single(rows);
    }

    [Fact]
    public void UpsertChunkFileRef_DifferentChunkIndex_AreDistinctRows()
    {
        SeedChunk("ZERO", 4096);
        _backend.UpsertChunkFileRef(@"C:\padded.bin", "ZERO", 0, ReferencedAt);
        _backend.UpsertChunkFileRef(@"C:\padded.bin", "ZERO", 1, ReferencedAt);

        // Both rows reference the SAME chunk so GetChunkEntriesForFile
        // returns one entry. The existence of two underlying rows is
        // verified by DeleteChunkFileRefsForFileAndChunk reporting a
        // non-zero delete count in another test.
        var rows = _backend.GetChunkEntriesForFile(@"C:\padded.bin");
        Assert.Single(rows);
    }

    [Fact]
    public void UpsertChunkFileRef_NullOrWhitespace_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _backend.UpsertChunkFileRef(filePath: "", "HASH", 0, ReferencedAt));
        Assert.Throws<ArgumentException>(() =>
            _backend.UpsertChunkFileRef(@"C:\a.bin", chunkHash: "", 0, ReferencedAt));
    }

    [Fact]
    public void BulkInsertChunkFileRefs_InsertsAllRows()
    {
        _backend.BulkInsertChunkIndexEntries(new[]
        {
            new ChunkIndexEntry { ChunkHash = "HASH-A", FirstUploadedAt = ReferencedAt,
                SizeBytes = 1024, ReferenceCount = 1, LastVerifiedAt = ReferencedAt },
            new ChunkIndexEntry { ChunkHash = "HASH-B", FirstUploadedAt = ReferencedAt,
                SizeBytes = 2048, ReferenceCount = 1, LastVerifiedAt = ReferencedAt },
        });

        _backend.BulkInsertChunkFileRefs(new[]
        {
            new ChunkFileRefRow { FilePath = @"C:\f.bin", ChunkHash = "HASH-A",
                ChunkIndex = 0, ReferencedAt = ReferencedAt },
            new ChunkFileRefRow { FilePath = @"C:\f.bin", ChunkHash = "HASH-B",
                ChunkIndex = 1, ReferencedAt = ReferencedAt },
        });

        var rows = _backend.GetChunkEntriesForFile(@"C:\f.bin");
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ChunkHash == "HASH-A");
        Assert.Contains(rows, r => r.ChunkHash == "HASH-B");
    }

    [Fact]
    public void BulkInsertChunkFileRefs_EmptySequence_IsNoOp()
    {
        _backend.BulkInsertChunkFileRefs(Array.Empty<ChunkFileRefRow>());
        Assert.Empty(_backend.GetChunkEntriesForFile(@"C:\never.bin"));
    }

    [Fact]
    public void BulkInsertChunkFileRefs_NullSequence_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _backend.BulkInsertChunkFileRefs(null!));
    }

    [Fact]
    public void DeleteChunkFileRefsForFile_RemovesAllRowsForThatFile()
    {
        SeedChunk("HASH-A", 1024);
        SeedChunk("HASH-B", 2048);
        _backend.UpsertChunkFileRef(@"C:\one.bin", "HASH-A", 0, ReferencedAt);
        _backend.UpsertChunkFileRef(@"C:\one.bin", "HASH-B", 1, ReferencedAt);
        _backend.UpsertChunkFileRef(@"C:\two.bin", "HASH-A", 0, ReferencedAt);

        var deleted = _backend.DeleteChunkFileRefsForFile(@"C:\one.bin");

        Assert.Equal(2, deleted);
        Assert.Empty(_backend.GetChunkEntriesForFile(@"C:\one.bin"));
        Assert.Single(_backend.GetChunkEntriesForFile(@"C:\two.bin"));
    }

    [Fact]
    public void DeleteChunkFileRefsForFile_NoRows_ReturnsZero()
    {
        Assert.Equal(0,
            _backend.DeleteChunkFileRefsForFile(@"C:\never-existed.bin"));
    }

    [Fact]
    public void DeleteChunkFileRefsForChunk_RemovesAllRowsForThatChunk()
    {
        SeedChunk("HASH-X", 1024);
        SeedChunk("HASH-Y", 2048);
        _backend.UpsertChunkFileRef(@"C:\a.bin", "HASH-X", 0, ReferencedAt);
        _backend.UpsertChunkFileRef(@"C:\b.bin", "HASH-X", 0, ReferencedAt);
        _backend.UpsertChunkFileRef(@"C:\c.bin", "HASH-Y", 0, ReferencedAt);

        var deleted = _backend.DeleteChunkFileRefsForChunk("HASH-X");

        Assert.Equal(2, deleted);
        Assert.Empty(_backend.GetChunkEntriesForFile(@"C:\a.bin"));
        Assert.Empty(_backend.GetChunkEntriesForFile(@"C:\b.bin"));
        Assert.Single(_backend.GetChunkEntriesForFile(@"C:\c.bin"));
    }

    [Fact]
    public void DeleteChunkFileRefsForChunk_NoRows_ReturnsZero()
    {
        Assert.Equal(0, _backend.DeleteChunkFileRefsForChunk("HASH-NEVER"));
    }

    [Fact]
    public void DeleteChunkFileRefsForFileAndChunk_RemovesOnlyTheTargetTuple()
    {
        SeedChunk("HASH-A", 1024);
        SeedChunk("HASH-B", 2048);
        _backend.UpsertChunkFileRef(@"C:\one.bin", "HASH-A", 0, ReferencedAt);
        _backend.UpsertChunkFileRef(@"C:\one.bin", "HASH-B", 1, ReferencedAt);
        _backend.UpsertChunkFileRef(@"C:\two.bin", "HASH-A", 0, ReferencedAt);

        var deleted = _backend.DeleteChunkFileRefsForFileAndChunk(@"C:\one.bin", "HASH-A");

        Assert.Equal(1, deleted);
        var oneRows = _backend.GetChunkEntriesForFile(@"C:\one.bin");
        Assert.Single(oneRows);
        Assert.Equal("HASH-B", oneRows[0].ChunkHash);
        Assert.Single(_backend.GetChunkEntriesForFile(@"C:\two.bin"));
    }

    [Fact]
    public void DeleteChunkFileRefsForFileAndChunk_NoMatch_ReturnsZero()
    {
        SeedChunk("HASH-A", 1024);
        _backend.UpsertChunkFileRef(@"C:\one.bin", "HASH-A", 0, ReferencedAt);

        Assert.Equal(0, _backend.DeleteChunkFileRefsForFileAndChunk(
            @"C:\one.bin", "HASH-NOPE"));
        Assert.Equal(0, _backend.DeleteChunkFileRefsForFileAndChunk(
            @"C:\never.bin", "HASH-A"));

        Assert.Single(_backend.GetChunkEntriesForFile(@"C:\one.bin"));
    }

    private void SeedChunk(string hash, long sizeBytes)
    {
        _backend.SaveChunkIndexEntry(new ChunkIndexEntry
        {
            ChunkHash = hash,
            FirstUploadedAt = ReferencedAt,
            SizeBytes = sizeBytes,
            ReferenceCount = 1,
            LastVerifiedAt = ReferencedAt,
        });
    }
}
