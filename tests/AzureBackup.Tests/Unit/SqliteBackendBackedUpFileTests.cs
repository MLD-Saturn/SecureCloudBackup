using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1d: round-trip tests for <see cref="BackedUpFile"/>
/// persistence on the SQLite backend, including the nested
/// <see cref="ChunkInfo"/> list that maps to the relational
/// <c>file_chunks</c> table.
///
/// <para>
/// The pure-relational schema decision (eval doc \u00a74) is exercised here:
/// every chunk becomes a row with <c>chunk_order</c> preserving the
/// original list index. SaveBackedUpFile must replace the entire chunk
/// list atomically, and reads must return chunks in their original
/// order.
/// </para>
/// </summary>
public class SqliteBackendBackedUpFileTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly SqliteBackend _backend;

    public SqliteBackendBackedUpFileTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "file.db");
        _backend = new InMemorySnapshotBackend();
        _backend.Initialize(_dbPath, "FileTestPwd!".AsSpan());
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    private static BackedUpFile MakeSampleFile(string path = @"C:\src\hello.txt") => new()
    {
        LocalPath = path,
        BlobName = "metadata/hello.txt.json",
        FileSize = 4096,
        LastModified = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc),
        FileHash = "ABCDEF0123456789",
        Status = BackupStatus.Completed,
        BackedUpAt = new DateTime(2026, 4, 17, 21, 31, 0, DateTimeKind.Utc),
        MetadataVersion = 2,
        Chunks =
        {
            new ChunkInfo { Index = 0, Offset = 0,    Length = 1024, Hash = "AAAA", BlobName = "chunks/AAAA" },
            new ChunkInfo { Index = 1, Offset = 1024, Length = 2048, Hash = "BBBB", BlobName = "chunks/BBBB" },
            new ChunkInfo { Index = 2, Offset = 3072, Length = 1024, Hash = "CCCC", BlobName = "chunks/CCCC" },
        },
    };

    [Fact]
    public void GetBackedUpFile_MissingPath_ReturnsNull()
    {
        Assert.Null(_backend.GetBackedUpFile(@"C:\does\not\exist"));
    }

    [Fact]
    public void SaveBackedUpFile_RoundTripsAllScalarsAndChunks()
    {
        // Arrange
        var saved = MakeSampleFile();

        // Act
        _backend.SaveBackedUpFile(saved);
        var loaded = _backend.GetBackedUpFile(saved.LocalPath);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(saved.LocalPath, loaded!.LocalPath);
        Assert.Equal(saved.BlobName, loaded.BlobName);
        Assert.Equal(saved.FileSize, loaded.FileSize);
        Assert.Equal(saved.LastModified, loaded.LastModified);
        Assert.Equal(saved.FileHash, loaded.FileHash);
        Assert.Equal(saved.Status, loaded.Status);
        Assert.Equal(saved.BackedUpAt, loaded.BackedUpAt);
        Assert.Equal(saved.MetadataVersion, loaded.MetadataVersion);

        // Chunks: order preserved, every field round-trips.
        Assert.Equal(3, loaded.Chunks.Count);
        for (var i = 0; i < saved.Chunks.Count; i++)
        {
            Assert.Equal(saved.Chunks[i].Index, loaded.Chunks[i].Index);
            Assert.Equal(saved.Chunks[i].Offset, loaded.Chunks[i].Offset);
            Assert.Equal(saved.Chunks[i].Length, loaded.Chunks[i].Length);
            Assert.Equal(saved.Chunks[i].Hash, loaded.Chunks[i].Hash);
            Assert.Equal(saved.Chunks[i].BlobName, loaded.Chunks[i].BlobName);
        }
    }

    [Fact]
    public void SaveBackedUpFile_AssignsIdOnInsert()
    {
        var saved = MakeSampleFile();
        Assert.Equal(0, saved.Id);

        _backend.SaveBackedUpFile(saved);

        Assert.True(saved.Id > 0, "Id should be populated from the auto-generated SQLite row id");
    }

    [Fact]
    public void SaveBackedUpFile_UpsertByLocalPath_ReusesId()
    {
        // Arrange: first save assigns an id.
        var first = MakeSampleFile();
        _backend.SaveBackedUpFile(first);
        var firstId = first.Id;

        // Act: a second save with the SAME LocalPath should update, not insert.
        var second = MakeSampleFile();
        second.FileSize = 99999;
        _backend.SaveBackedUpFile(second);

        // Assert
        Assert.Equal(firstId, second.Id);
        var loaded = _backend.GetBackedUpFile(first.LocalPath);
        Assert.NotNull(loaded);
        Assert.Equal(99999, loaded!.FileSize);
    }

    [Fact]
    public void SaveBackedUpFile_OverwritesPreviousChunkList()
    {
        // Arrange: save with 3 chunks.
        var first = MakeSampleFile();
        _backend.SaveBackedUpFile(first);

        // Act: re-save with 1 chunk only.
        var second = MakeSampleFile();
        second.Chunks.Clear();
        second.Chunks.Add(new ChunkInfo
        {
            Index = 0,
            Offset = 0,
            Length = 64,
            Hash = "DDDD",
            BlobName = "chunks/DDDD",
        });
        _backend.SaveBackedUpFile(second);

        // Assert: only the new chunk remains.
        var loaded = _backend.GetBackedUpFile(first.LocalPath);
        Assert.NotNull(loaded);
        var only = Assert.Single(loaded!.Chunks);
        Assert.Equal("DDDD", only.Hash);
    }

    [Fact]
    public void SaveBackedUpFile_EmptyChunkList_PersistsZeroChunks()
    {
        // Arrange
        var saved = MakeSampleFile();
        saved.Chunks.Clear();

        // Act
        _backend.SaveBackedUpFile(saved);
        var loaded = _backend.GetBackedUpFile(saved.LocalPath);

        // Assert
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.Chunks);
    }

    [Fact]
    public void GetAllBackedUpFiles_ReturnsAllRowsWithChunks()
    {
        // Arrange: three different files.
        _backend.SaveBackedUpFile(MakeSampleFile(@"C:\a.txt"));
        _backend.SaveBackedUpFile(MakeSampleFile(@"C:\b.txt"));
        _backend.SaveBackedUpFile(MakeSampleFile(@"C:\c.txt"));

        // Act
        var all = _backend.GetAllBackedUpFiles();

        // Assert: every file present, every file has its chunks.
        Assert.Equal(3, all.Count);
        Assert.All(all, f => Assert.Equal(3, f.Chunks.Count));
        Assert.Contains(all, f => f.LocalPath == @"C:\a.txt");
        Assert.Contains(all, f => f.LocalPath == @"C:\b.txt");
        Assert.Contains(all, f => f.LocalPath == @"C:\c.txt");
    }

    [Fact]
    public void GetAllBackedUpFiles_EmptyDatabase_ReturnsEmptyList()
    {
        Assert.Empty(_backend.GetAllBackedUpFiles());
    }

    [Fact]
    public void SaveBackedUpFile_SurvivesReopen()
    {
        // Arrange
        var saved = MakeSampleFile();
        _backend.SaveBackedUpFile(saved);
        _backend.Dispose();

        // Act
        using var reopened = new InMemorySnapshotBackend();
        reopened.Initialize(_dbPath, "FileTestPwd!".AsSpan());
        var loaded = reopened.GetBackedUpFile(saved.LocalPath);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Chunks.Count);
        Assert.Equal("ABCDEF0123456789", loaded.FileHash);
    }

    [Fact]
    public void SaveBackedUpFile_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _backend.SaveBackedUpFile(null!));
    }

    [Fact]
    public void GetBackedUpFile_NullOrWhitespacePath_Throws()
    {
        Assert.Throws<ArgumentException>(() => _backend.GetBackedUpFile(""));
        Assert.Throws<ArgumentException>(() => _backend.GetBackedUpFile("   "));
    }
}
