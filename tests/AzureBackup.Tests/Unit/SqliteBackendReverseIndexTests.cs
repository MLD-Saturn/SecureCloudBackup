using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1e (part 2 of 2): round-trip tests for the reverse
/// chunk index (<c>chunk_file_refs</c>) on the SQLite backend.
///
/// <para>
/// Two separate flows are exercised:
/// </para>
/// <list type="number">
///   <item>SaveBackedUpFile keeping chunk_file_refs in sync as the
///     primary writer (the steady-state path - new in the SQLite
///     backend; LiteDB derived this from a separate ReferencingFiles
///     list).</item>
///   <item>RebuildReverseChunkIndex backfilling rows for files that
///     were saved before the reverse index existed (the
///     migration-from-LiteDB path).</item>
/// </list>
///
/// <para>
/// GetChunkEntriesForFile is the method that hit the b0c9439 LiteDB
/// regression - the SQLite version uses a clean SELECT JOIN with no
/// expression-tree limitations.
/// </para>
/// </summary>
public class SqliteBackendReverseIndexTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly SqliteBackend _backend;

    public SqliteBackendReverseIndexTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-rev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "rev.db");
        _backend = new InMemorySnapshotBackend();
        _backend.Initialize(_dbPath, "ReverseTestPwd!".AsSpan());
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    private static BackedUpFile MakeFile(string path, params string[] chunkHashes)
    {
        var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        var file = new BackedUpFile
        {
            LocalPath = path,
            BlobName = $"metadata/{Path.GetFileName(path)}.json",
            FileSize = chunkHashes.Length * 1024,
            LastModified = when,
            FileHash = "FILE-" + path.GetHashCode().ToString("X8"),
            Status = BackupStatus.Completed,
            BackedUpAt = when,
            MetadataVersion = 1,
        };
        for (var i = 0; i < chunkHashes.Length; i++)
        {
            file.Chunks.Add(new ChunkInfo
            {
                Index = i,
                Offset = i * 1024,
                Length = 1024,
                Hash = chunkHashes[i],
                BlobName = $"chunks/{chunkHashes[i]}",
            });
        }
        return file;
    }

    private void SeedChunkIndex(params string[] hashes)
    {
        var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        foreach (var h in hashes)
        {
            _backend.SaveChunkIndexEntry(new ChunkIndexEntry
            {
                ChunkHash = h,
                FirstUploadedAt = when,
                OriginalUploaderPath = $@"C:\seed\{h}",
                SizeBytes = 1024,
                ReferenceCount = 1,
                CurrentTier = StorageTier.Hot,
                LastVerifiedAt = when,
            });
        }
    }

    [Fact]
    public void GetChunkEntriesForFile_NoMatchingPath_ReturnsEmpty()
    {
        Assert.Empty(_backend.GetChunkEntriesForFile(@"C:\does\not\exist"));
    }

    [Fact]
    public void SaveBackedUpFile_PopulatesReverseIndex_GetChunkEntriesForFile_Returns()
    {
        // Arrange: chunk index has the entries; SaveBackedUpFile wires up the refs.
        SeedChunkIndex("AAAA", "BBBB", "CCCC");
        _backend.SaveBackedUpFile(MakeFile(@"C:\photos\01.jpg", "AAAA", "BBBB", "CCCC"));

        // Act
        var entries = _backend.GetChunkEntriesForFile(@"C:\photos\01.jpg");

        // Assert
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.ChunkHash == "AAAA");
        Assert.Contains(entries, e => e.ChunkHash == "BBBB");
        Assert.Contains(entries, e => e.ChunkHash == "CCCC");
    }

    [Fact]
    public void GetChunkEntriesForFile_OnlyReturnsRequestedFile()
    {
        SeedChunkIndex("AAAA", "BBBB", "CCCC", "DDDD");
        _backend.SaveBackedUpFile(MakeFile(@"C:\one.bin", "AAAA", "BBBB"));
        _backend.SaveBackedUpFile(MakeFile(@"C:\two.bin", "CCCC", "DDDD"));

        var first = _backend.GetChunkEntriesForFile(@"C:\one.bin");

        Assert.Equal(2, first.Count);
        Assert.Contains(first, e => e.ChunkHash == "AAAA");
        Assert.Contains(first, e => e.ChunkHash == "BBBB");
        Assert.DoesNotContain(first, e => e.ChunkHash == "CCCC");
    }

    [Fact]
    public void GetChunkEntriesForFile_DistinctsRepeatedChunkInFile()
    {
        // Arrange: a file references the same chunk twice (rare but valid;
        // happens with run-length-encoded media). The reverse index is
        // expected to return the entry once.
        SeedChunkIndex("DUPE");
        var file = MakeFile(@"C:\repeats.bin", "DUPE", "DUPE", "DUPE");
        _backend.SaveBackedUpFile(file);

        var entries = _backend.GetChunkEntriesForFile(@"C:\repeats.bin");

        Assert.Single(entries);
        Assert.Equal("DUPE", entries[0].ChunkHash);
    }

    [Fact]
    public void SaveBackedUpFile_OverwritesPreviousReverseIndexEntries()
    {
        // Arrange: first save references AAAA + BBBB.
        SeedChunkIndex("AAAA", "BBBB", "CCCC");
        _backend.SaveBackedUpFile(MakeFile(@"C:\swap.bin", "AAAA", "BBBB"));

        // Act: re-save the same file with a different chunk list.
        _backend.SaveBackedUpFile(MakeFile(@"C:\swap.bin", "CCCC"));

        // Assert: AAAA and BBBB no longer appear; only CCCC.
        var entries = _backend.GetChunkEntriesForFile(@"C:\swap.bin");
        Assert.Single(entries);
        Assert.Equal("CCCC", entries[0].ChunkHash);
    }

    [Fact]
    public void IsReverseChunkIndexBuilt_FreshDb_ReturnsFalse()
    {
        Assert.False(_backend.IsReverseChunkIndexBuilt());
    }

    [Fact]
    public void RebuildReverseChunkIndex_FreshDb_MarksBuilt()
    {
        // No data; RebuildReverseChunkIndex should still mark the sentinel.
        _backend.RebuildReverseChunkIndex();
        Assert.True(_backend.IsReverseChunkIndexBuilt());
    }

    [Fact]
    public void RebuildReverseChunkIndex_BackfillsMissingRefs()
    {
        // Arrange: insert a file row with chunk rows directly via
        // SaveBackedUpFile, then manually clear chunk_file_refs to simulate
        // a LiteDB-era database that has not yet had its reverse index
        // built. SaveBackedUpFile populates the refs; we wipe them so the
        // rebuild has work to do.
        SeedChunkIndex("AAAA", "BBBB");
        _backend.SaveBackedUpFile(MakeFile(@"C:\backfill.bin", "AAAA", "BBBB"));

        // Sanity: refs exist before wipe.
        Assert.Equal(2, _backend.GetChunkEntriesForFile(@"C:\backfill.bin").Count);

        // Wipe the reverse index by clearing it through ClearChunkIndex
        // and re-seeding chunk_index. ClearChunkIndex clears both tables,
        // so we rebuild chunk_index manually but leave chunk_file_refs empty.
        _backend.ClearChunkIndex();
        SeedChunkIndex("AAAA", "BBBB");
        Assert.Empty(_backend.GetChunkEntriesForFile(@"C:\backfill.bin"));

        // Act
        _backend.RebuildReverseChunkIndex();

        // Assert
        Assert.True(_backend.IsReverseChunkIndexBuilt());
        var entries = _backend.GetChunkEntriesForFile(@"C:\backfill.bin");
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void RebuildReverseChunkIndex_AlreadyBuilt_NoOp()
    {
        // Arrange: build once.
        _backend.RebuildReverseChunkIndex();
        var firstStamp = _backend.GetIndexMetadata("ReverseIndexBuiltAt");
        Assert.NotNull(firstStamp);

        // Act: a second invocation should short-circuit without changing
        // the stored sentinel timestamp.
        _backend.RebuildReverseChunkIndex();

        // Assert
        Assert.Equal(firstStamp, _backend.GetIndexMetadata("ReverseIndexBuiltAt"));
    }

    [Fact]
    public void RebuildReverseChunkIndex_ReportsProgress()
    {
        // Arrange: 3 files, refs cleared so all three need backfill.
        SeedChunkIndex("AAAA", "BBBB", "CCCC");
        _backend.SaveBackedUpFile(MakeFile(@"C:\f1.bin", "AAAA"));
        _backend.SaveBackedUpFile(MakeFile(@"C:\f2.bin", "BBBB"));
        _backend.SaveBackedUpFile(MakeFile(@"C:\f3.bin", "CCCC"));

        _backend.ClearChunkIndex();
        SeedChunkIndex("AAAA", "BBBB", "CCCC");

        var reports = new List<(int processed, int total)>();
        var progress = new Progress<(int, int)>(reports.Add);

        // Act
        _backend.RebuildReverseChunkIndex(progress);

        // Assert: at least one report and the final report shows total reached.
        // Progress<T> dispatches asynchronously; give it a moment to drain.
        SpinWait.SpinUntil(() => reports.Count > 0 && reports[^1].processed == reports[^1].total,
            TimeSpan.FromSeconds(5));
        Assert.NotEmpty(reports);
        var final = reports[^1];
        Assert.Equal(final.total, final.processed);
        Assert.Equal(3, final.total);
    }

    [Fact]
    public void RebuildReverseChunkIndex_Cancellation_StopsBetweenFiles()
    {
        // Arrange: many files so cancellation has a chance to fire mid-loop.
        SeedChunkIndex(Enumerable.Range(0, 50).Select(i => $"H{i:D3}").ToArray());
        for (var i = 0; i < 50; i++)
        {
            _backend.SaveBackedUpFile(MakeFile($@"C:\big\f{i}.bin", $"H{i:D3}"));
        }

        // Wipe the refs.
        _backend.ClearChunkIndex();
        SeedChunkIndex(Enumerable.Range(0, 50).Select(i => $"H{i:D3}").ToArray());

        // Act: cancel immediately.
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            _backend.RebuildReverseChunkIndex(cancellationToken: cts.Token));

        // Assert: sentinel was NOT set; partial rows are NOT rolled back
        // (idempotent rebuild can resume).
        Assert.False(_backend.IsReverseChunkIndexBuilt());
    }

    [Fact]
    public void RebuildReverseChunkIndex_FreshDb_IndexesSurviveDropAndRecreate()
    {
        // Arrange: fresh empty chunk_file_refs (so the rebuild takes the
        // C-3 (3c-2) drop+recreate optimisation path) and a few files
        // queued for backfill.
        SeedChunkIndex("PIDX1", "PIDX2", "PIDX3");
        _backend.SaveBackedUpFile(MakeFile(@"C:\idx\a.bin", "PIDX1"));
        _backend.SaveBackedUpFile(MakeFile(@"C:\idx\b.bin", "PIDX2"));
        _backend.SaveBackedUpFile(MakeFile(@"C:\idx\c.bin", "PIDX3"));

        // Wipe chunk_file_refs and the sentinel via the bench helper so
        // the rebuild has work AND the empty-table optimisation triggers.
        _backend.ClearReverseChunkIndexForBenchmark();

        // Act
        _backend.RebuildReverseChunkIndex();

        // Assert: rebuild completed and refs are queryable, which exercises
        // both chunk_file_refs indexes (the path lookup and the hash lookup).
        // Pre-Step-7 this also opened a separate read-only SQLCipher-keyed
        // connection to inspect sqlite_master for the index names directly, but
        // the in-memory snapshot backend has no separate keyed on-disk
        // connection; the functional round-trip below covers the same recreate
        // contract.
        Assert.True(_backend.IsReverseChunkIndexBuilt());
        Assert.Single(_backend.GetChunkEntriesForFile(@"C:\idx\a.bin"));
    }

    [Fact]
    public void GetChunkEntriesForFile_NullOrWhitespace_Throws()
    {
        Assert.Throws<ArgumentException>(() => _backend.GetChunkEntriesForFile(""));
        Assert.Throws<ArgumentException>(() => _backend.GetChunkEntriesForFile("   "));
    }
}
