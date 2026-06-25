using System.Security.Cryptography;
using SecureCloudBackup.Core.Models;
using SecureCloudBackup.Core.Services;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Integration tests for orphan detection and cleanup functionality.
/// </summary>
public class OrphanDetectionIntegrationTests : IAsyncLifetime
{
    private const string TestPassword = "TestPassword123!";
    private string _testDbPath = null!;
    private LocalDatabaseService _databaseService = null!;
    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;
    private ChunkIndexService _indexService = null!;

    public async Task InitializeAsync()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"OrphanTest_{Guid.NewGuid()}.db");
        
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_testDbPath, TestPassword);
        
        _encryptionService = new EncryptionService();
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);
        CryptographicOperations.ZeroMemory(key);
        
        _blobService = new InMemoryBlobService(_encryptionService);
        await _blobService.ConnectAsync("test-connection", "test-container");
        
        _indexService = new ChunkIndexService(_databaseService, _blobService, _encryptionService);
    }

    public Task DisposeAsync()
    {
        _encryptionService?.Dispose();
        _databaseService?.Dispose();
        try
        {
            if (File.Exists(_testDbPath))
                File.Delete(_testDbPath);
        }
        catch { /* Ignore cleanup errors */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ScanForOrphansAsync_FindsChunksNotInIndex()
    {
        // Arrange - Upload a chunk directly without adding to index
        var orphanHash = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        await _blobService.UploadChunkDirectAsync(chunkData, orphanHash, StorageTier.Cool);
        
        // Also add a properly tracked chunk
        var trackedHash = "b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3";
        await _blobService.UploadChunkDirectAsync(chunkData, trackedHash, StorageTier.Cool);
        _indexService.AddReference(trackedHash, @"C:\file.txt", 0, 1024, StorageTier.Cool, true);
        
        // Act
        var result = await _indexService.ScanForOrphansAsync();
        
        // Assert
        Assert.Single(result.OrphanedChunks);
        Assert.Contains(result.OrphanedChunks, o => o.ChunkHash == orphanHash);
    }

    [Fact]
    public async Task CleanupOrphansAsync_DeletesOrphanedChunks()
    {
        // Arrange - Upload an orphan chunk
        var orphanHash = "c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";
        var chunkData = new byte[2048];
        Random.Shared.NextBytes(chunkData);
        await _blobService.UploadChunkDirectAsync(chunkData, orphanHash, StorageTier.Cool);
        
        // Create an orphan entry
        var orphanEntry = new ChunkIndexEntry
        {
            ChunkHash = orphanHash,
            SizeBytes = 2048,
            CurrentTier = StorageTier.Cool,
            ReferenceCount = 0,
            ReferencingFiles = []
        };
        
        // Act
        var result = await _indexService.CleanupOrphansAsync(new[] { orphanEntry });
        
        // Assert
        Assert.Equal(1, result.ChunksDeleted);
        Assert.Equal(2048, result.BytesFreed);
        Assert.Equal(0, result.FailedDeletions);
        
        // Verify chunk is actually deleted
        var exists = await _blobService.BlobExistsAsync($"chunks/{orphanHash}");
        Assert.False(exists);
    }

    [Fact]
    public async Task CleanupOrphansAsync_ChunkReferencedAgainAfterScan_SkipsDeletion()
    {
        // Cause 3 mitigation: the orphan scan computes its "0 references"
        // verdict from a snapshot. A backup (or dedup hit) that references
        // the chunk again AFTER the scan but BEFORE cleanup must not lead
        // to deletion -- doing so would orphan a live file and surface as
        // a missing-blob restore failure later. CleanupOrphansAsync now
        // re-checks the authoritative reverse index immediately before
        // deleting and skips any candidate that became referenced again.
        var hash = "e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6";
        var chunkData = new byte[4096];
        Random.Shared.NextBytes(chunkData);
        await _blobService.UploadChunkDirectAsync(chunkData, hash, StorageTier.Cool);

        // The stale scan verdict: an orphan entry with 0 references.
        var staleOrphanEntry = new ChunkIndexEntry
        {
            ChunkHash = hash,
            SizeBytes = 4096,
            CurrentTier = StorageTier.Cool,
            ReferenceCount = 0,
            ReferencingFiles = []
        };

        // Simulate the race: a backup references the chunk again between
        // the scan and the cleanup call.
        _indexService.AddReference(hash, @"C:\reappeared.txt", 0, 4096, StorageTier.Cool, true);
        Assert.Equal(1, _databaseService.GetReferenceCountForChunk(hash));

        // Act
        var result = await _indexService.CleanupOrphansAsync(new[] { staleOrphanEntry });

        // Assert - the chunk was NOT deleted and was counted as skipped.
        Assert.Equal(0, result.ChunksDeleted);
        Assert.Equal(0, result.FailedDeletions);
        Assert.Equal(1, result.SkippedStillReferenced);
        Assert.Equal(0, result.BytesFreed);

        var exists = await _blobService.BlobExistsAsync($"chunks/{hash}");
        Assert.True(exists, "Re-referenced chunk must survive cleanup");
    }

    [Fact]
    public async Task FullOrphanDetectionWorkflow_EndToEnd()
    {
        // Arrange - Simulate a file backup then deletion scenario
        var hash1 = "d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5";
        var hash2 = "e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6";
        var filePath = @"C:\TestFiles\document.txt";
        
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        
        // Upload chunks and track in index (simulating backup)
        await _blobService.UploadChunkDirectAsync(chunkData, hash1, StorageTier.Cool);
        await _blobService.UploadChunkDirectAsync(chunkData, hash2, StorageTier.Cool);
        _indexService.AddReference(hash1, filePath, 0, 1024, StorageTier.Cool, true);
        _indexService.AddReference(hash2, filePath, 1, 1024, StorageTier.Cool, true);
        
        // Verify no orphans initially
        var initialScan = await _indexService.ScanForOrphansAsync();
        Assert.Empty(initialScan.OrphanedChunks);
        
        // Act - Remove file references (simulating file deletion)
        await _indexService.RemoveFileReferencesAsync(filePath);
        
        // Verify chunks were deleted automatically (since ref count = 0)
        var exists1 = await _blobService.BlobExistsAsync($"chunks/{hash1}");
        var exists2 = await _blobService.BlobExistsAsync($"chunks/{hash2}");
        Assert.False(exists1);
        Assert.False(exists2);
        
        // Also verify index entries are removed
        Assert.Null(_databaseService.GetChunkIndexEntry(hash1));
        Assert.Null(_databaseService.GetChunkIndexEntry(hash2));
    }

    [Fact]
    public async Task SharedChunks_NotDeletedWhenOneFileRemoved()
    {
        // Arrange - Two files sharing a chunk
        var sharedHash = "f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1";
        var file1 = @"C:\file1.txt";
        var file2 = @"C:\file2.txt";
        
        var chunkData = new byte[1024];
        Random.Shared.NextBytes(chunkData);
        await _blobService.UploadChunkDirectAsync(chunkData, sharedHash, StorageTier.Cool);
        
        // Both files reference the same chunk
        _indexService.AddReference(sharedHash, file1, 0, 1024, StorageTier.Cool, true);
        _indexService.AddReference(sharedHash, file2, 0, 1024, StorageTier.Cool, false);
        
        // Act - Remove one file's references
        var deleted = await _indexService.RemoveFileReferencesAsync(file1);
        
        // Assert - Chunk should NOT be deleted (still referenced by file2)
        Assert.Equal(0, deleted);
        var entry = _databaseService.GetChunkIndexEntry(sharedHash);
        Assert.NotNull(entry);
        Assert.Equal(1, entry.ReferenceCount);
        
        var exists = await _blobService.BlobExistsAsync($"chunks/{sharedHash}");
        Assert.True(exists);
    }

    [Fact]
    public void GetIndexSummary_ReflectsOrphanStatistics()
    {
        // Arrange - Create entries with various states
        var hash1 = "a1a2a3a4a5a6a1a2a3a4a5a6a1a2a3a4a5a6a1a2a3a4a5a6a1a2a3a4a5a6a1a2";
        var hash2 = "b1b2b3b4b5b6b1b2b3b4b5b6b1b2b3b4b5b6b1b2b3b4b5b6b1b2b3b4b5b6b1b2";
        
        // Normal chunk with reference
        _indexService.AddReference(hash1, @"C:\file.txt", 0, 1000, StorageTier.Hot, true);
        
        // Orphan chunk (manually create with 0 refs)
        var orphanEntry = new ChunkIndexEntry
        {
            ChunkHash = hash2,
            SizeBytes = 500,
            CurrentTier = StorageTier.Cold,
            ReferenceCount = 0,
            ReferencingFiles = [],
            FirstUploadedAt = DateTime.UtcNow
        };
        _databaseService.SaveChunkIndexEntry(orphanEntry);
        
        // Act
        var summary = _indexService.GetIndexSummary();
        
        // Assert
        Assert.Equal(2, summary.TotalChunks);
        Assert.Equal(1500, summary.TotalSizeBytes);
        Assert.Equal(1, summary.OrphanCount);
        Assert.Equal(500, summary.OrphanSizeBytes);
        Assert.Contains(StorageTier.Hot, summary.TierBreakdown.Keys);
        Assert.Contains(StorageTier.Cold, summary.TierBreakdown.Keys);
    }

    /// <summary>
    /// C-5: BackupIndexToAzureAsync + RestoreIndexFromAzureAsync must
    /// preserve the chunk_file_refs reverse-index. Pre-C-5 the v1
    /// backup carried only ChunkIndexEntry rows and relied on the
    /// LiteDB-era ReferencingFiles list to reconstruct refs on
    /// restore. Under SQLite GetAllChunkIndexEntries leaves
    /// ReferencingFiles empty by design, so a v1 round-trip silently
    /// dropped every reverse-index row. v2 carries chunk_file_refs
    /// alongside the entries and the restore path replays them.
    /// </summary>
    [Fact]
    public async Task BackupAndRestoreIndex_PreservesReverseIndex()
    {
        var sharedHash = "f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1";
        var soloHash = "0102030405060708010203040506070801020304050607080102030405060708";
        var file1 = @"C:\backupRestore\file1.txt";
        var file2 = @"C:\backupRestore\file2.txt";

        // Seed: shared chunk referenced by both files; solo chunk by file2.
        var sharedData = new byte[1024];
        var soloData = new byte[1024];
        Random.Shared.NextBytes(sharedData);
        Random.Shared.NextBytes(soloData);
        await _blobService.UploadChunkDirectAsync(sharedData, sharedHash, StorageTier.Cool);
        await _blobService.UploadChunkDirectAsync(soloData, soloHash, StorageTier.Cool);

        _indexService.AddReference(sharedHash, file1, 0, 1024, StorageTier.Cool, true);
        _indexService.AddReference(sharedHash, file2, 0, 1024, StorageTier.Cool, false);
        _indexService.AddReference(soloHash, file2, 1, 1024, StorageTier.Cool, true);

        // Sanity: chunk_file_refs has the expected 3 rows pre-backup.
        Assert.Equal(2, _databaseService.GetReferenceCountForChunk(sharedHash));
        Assert.Equal(1, _databaseService.GetReferenceCountForChunk(soloHash));

        // Act: backup -> wipe + restore (RestoreIndexFromAzureAsync
        // already calls ClearChunkIndex internally).
        await _indexService.BackupIndexToAzureAsync();
        var ok = await _indexService.RestoreIndexFromAzureAsync();
        Assert.True(ok);

        // Assert: reference counts and the reverse-index reflect the
        // pre-backup state, not zero.
        Assert.Equal(2, _databaseService.GetReferenceCountForChunk(sharedHash));
        Assert.Equal(1, _databaseService.GetReferenceCountForChunk(soloHash));

        var sharedRefs = _databaseService.GetReferencingFilesForChunk(sharedHash);
        Assert.Equal(2, sharedRefs.Count);
        Assert.Contains(sharedRefs, r => r.FilePath == file1);
        Assert.Contains(sharedRefs, r => r.FilePath == file2);

        var soloRefs = _databaseService.GetReferencingFilesForChunk(soloHash);
        Assert.Single(soloRefs);
        Assert.Equal(file2, soloRefs[0].FilePath);

        // GetChunkEntriesForFile is the lookup the orchestrator uses
        // to remove a file's references; if the reverse index were
        // empty after restore, RemoveFileReferencesAsync would be a
        // silent no-op.
        var file2Entries = _databaseService.GetChunkEntriesForFile(file2);
        Assert.Equal(2, file2Entries.Count);
    }
}
