using System.Security.Cryptography;
using SecureCloudBackup.Core.Models;
using SecureCloudBackup.Core.Services;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Tests for the blob tier migration operations used by the Migration tab.
/// These exercise the same Core service calls that TierMigrationViewModel.MigrateSelectedAsync
/// orchestrates: SetBlobTierAsync on each chunk, UploadFileMetadataAsync at the new tier,
/// and GetBlobPropertiesAsync to verify the result.
/// </summary>
public class TierMigrationOperationsTests : IAsyncLifetime
{
    private const string TestPassword = "TestPassword123!";
    private string _testDbPath = null!;
    private LocalDatabaseService _databaseService = null!;
    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;

    private const string ChunkHash1 = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
    private const string ChunkHash2 = "b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3";
    private const string ChunkHash3 = "c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4";

    public async Task InitializeAsync()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"TierMigrationOpsTest_{Guid.NewGuid()}.db");

        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_testDbPath, TestPassword);

        _encryptionService = new EncryptionService();
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);
        CryptographicOperations.ZeroMemory(key);

        _blobService = new InMemoryBlobService(_encryptionService);
        await _blobService.ConnectAsync("test-connection", "test-container");
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

    private async Task UploadChunkAsync(string chunkHash, int size, StorageTier tier)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        await _blobService.UploadChunkAsync(data, chunkHash, tier);
    }

    private static BackedUpFile CreateTestFile(string localPath, StorageTier tier, params (string hash, int length)[] chunks)
    {
        return new BackedUpFile
        {
            LocalPath = localPath,
            FileSize = chunks.Sum(c => (long)c.length),
            LastModified = DateTime.Now,
            FileHash = "filehash",
            CurrentStorageTier = tier,
            Chunks = chunks.Select((c, i) => new ChunkInfo
            {
                Index = i,
                Hash = c.hash,
                Offset = chunks.Take(i).Sum(x => (long)x.length),
                Length = c.length,
                BlobName = $"chunks/{c.hash}"
            }).ToList()
        };
    }

    #region SetBlobTierAsync — Single Chunk

    [Fact]
    public async Task SetBlobTierAsync_ChangesChunkFromHotToCool()
    {
        // Arrange
        await UploadChunkAsync(ChunkHash1, 1024, StorageTier.Hot);

        // Act
        await _blobService.SetBlobTierAsync($"chunks/{ChunkHash1}", StorageTier.Cool);

        // Assert
        var (_, tier) = await _blobService.GetBlobPropertiesAsync($"chunks/{ChunkHash1}");
        Assert.Equal(StorageTier.Cool, tier);
    }

    [Fact]
    public async Task SetBlobTierAsync_ChangesChunkFromHotToCold()
    {
        // Arrange
        await UploadChunkAsync(ChunkHash1, 1024, StorageTier.Hot);

        // Act
        await _blobService.SetBlobTierAsync($"chunks/{ChunkHash1}", StorageTier.Cold);

        // Assert
        var (_, tier) = await _blobService.GetBlobPropertiesAsync($"chunks/{ChunkHash1}");
        Assert.Equal(StorageTier.Cold, tier);
    }

    [Fact]
    public async Task SetBlobTierAsync_ChangesChunkFromHotToArchive()
    {
        // Arrange
        await UploadChunkAsync(ChunkHash1, 1024, StorageTier.Hot);

        // Act
        await _blobService.SetBlobTierAsync($"chunks/{ChunkHash1}", StorageTier.Archive);

        // Assert
        var (_, tier) = await _blobService.GetBlobPropertiesAsync($"chunks/{ChunkHash1}");
        Assert.Equal(StorageTier.Archive, tier);
    }

    #endregion

    #region SetBlobTierAsync — Multiple Chunks (Migration Workflow)

    [Fact]
    public async Task MigrationWorkflow_ChangesAllChunkTiers()
    {
        // Arrange — simulate a file with 3 chunks at Hot
        await UploadChunkAsync(ChunkHash1, 1024, StorageTier.Hot);
        await UploadChunkAsync(ChunkHash2, 2048, StorageTier.Hot);
        await UploadChunkAsync(ChunkHash3, 512, StorageTier.Hot);

        var file = CreateTestFile(@"C:\bigfile.txt", StorageTier.Hot,
            (ChunkHash1, 1024), (ChunkHash2, 2048), (ChunkHash3, 512));

        // Act — same loop as MigrateSelectedAsync
        foreach (var chunk in file.Chunks)
        {
            var blobName = string.IsNullOrEmpty(chunk.BlobName) ? $"chunks/{chunk.Hash}" : chunk.BlobName;
            await _blobService.SetBlobTierAsync(blobName, StorageTier.Cold);
        }

        // Assert — all chunks moved to Cold
        var (_, tier1) = await _blobService.GetBlobPropertiesAsync($"chunks/{ChunkHash1}");
        var (_, tier2) = await _blobService.GetBlobPropertiesAsync($"chunks/{ChunkHash2}");
        var (_, tier3) = await _blobService.GetBlobPropertiesAsync($"chunks/{ChunkHash3}");
        Assert.Equal(StorageTier.Cold, tier1);
        Assert.Equal(StorageTier.Cold, tier2);
        Assert.Equal(StorageTier.Cold, tier3);
    }

    [Fact]
    public async Task MigrationWorkflow_OnlyChangesSelectedFileChunks()
    {
        // Arrange — two files, each with one chunk
        await UploadChunkAsync(ChunkHash1, 1024, StorageTier.Hot);
        await UploadChunkAsync(ChunkHash2, 2048, StorageTier.Hot);

        var selectedFile = CreateTestFile(@"C:\selected.txt", StorageTier.Hot, (ChunkHash1, 1024));
        // ChunkHash2 belongs to another file that is NOT selected

        // Act — migrate only the selected file's chunks
        foreach (var chunk in selectedFile.Chunks)
        {
            var blobName = string.IsNullOrEmpty(chunk.BlobName) ? $"chunks/{chunk.Hash}" : chunk.BlobName;
            await _blobService.SetBlobTierAsync(blobName, StorageTier.Cool);
        }

        // Assert — only selected file's chunk changed
        var (_, tier1) = await _blobService.GetBlobPropertiesAsync($"chunks/{ChunkHash1}");
        var (_, tier2) = await _blobService.GetBlobPropertiesAsync($"chunks/{ChunkHash2}");
        Assert.Equal(StorageTier.Cool, tier1);
        Assert.Equal(StorageTier.Hot, tier2);
    }

    #endregion

    #region Metadata Re-upload at New Tier

    [Fact]
    public async Task MigrationWorkflow_ReUploadsMetadataAtNewTier()
    {
        // Arrange
        await UploadChunkAsync(ChunkHash1, 1024, StorageTier.Hot);
        var file = CreateTestFile(@"C:\file.txt", StorageTier.Hot, (ChunkHash1, 1024));
        await _blobService.UploadFileMetadataAsync(file, StorageTier.Hot);

        var opsBeforeMigration = _blobService.TotalOperations;

        // Act — change chunk tier then re-upload metadata (same as MigrateSelectedAsync)
        await _blobService.SetBlobTierAsync($"chunks/{ChunkHash1}", StorageTier.Cool);
        await _blobService.UploadFileMetadataAsync(file, StorageTier.Cool);
        file.CurrentStorageTier = StorageTier.Cool;

        // Assert
        Assert.Equal(StorageTier.Cool, file.CurrentStorageTier);
        Assert.True(_blobService.TotalOperations > opsBeforeMigration,
            "Expected blob operations for tier change and metadata re-upload");
    }

    [Fact]
    public async Task MigrationWorkflow_MetadataCanBeReDownloadedAfterReUpload()
    {
        // Arrange — upload chunk and metadata at Hot
        await UploadChunkAsync(ChunkHash1, 1024, StorageTier.Hot);
        var file = CreateTestFile(@"C:\file.txt", StorageTier.Hot, (ChunkHash1, 1024));
        await _blobService.UploadFileMetadataAsync(file, StorageTier.Hot);

        // Act — re-upload metadata at Cool tier (simulates migration)
        await _blobService.UploadFileMetadataAsync(file, StorageTier.Cool);

        // Assert — metadata still downloadable
        var blobs = await _blobService.ListMetadataBlobsAsync();
        Assert.NotEmpty(blobs);

        var downloaded = await _blobService.DownloadFileMetadataAsync(blobs[0]);
        Assert.NotNull(downloaded);
        Assert.Equal(file.LocalPath, downloaded!.LocalPath);
        Assert.Equal(file.FileSize, downloaded.FileSize);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task SetBlobTierAsync_NonexistentBlob_Throws()
    {
        // Act & Assert — setting tier on a blob that doesn't exist should fail
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _blobService.SetBlobTierAsync("chunks/nonexistent", StorageTier.Cool));
    }

    [Fact]
    public async Task SetBlobTierAsync_SameTier_Succeeds()
    {
        // Arrange — upload at Hot
        await UploadChunkAsync(ChunkHash1, 1024, StorageTier.Hot);

        // Act — set to same tier (no-op but should not throw)
        await _blobService.SetBlobTierAsync($"chunks/{ChunkHash1}", StorageTier.Hot);

        // Assert
        var (_, tier) = await _blobService.GetBlobPropertiesAsync($"chunks/{ChunkHash1}");
        Assert.Equal(StorageTier.Hot, tier);
    }

    [Fact]
    public async Task SetBlobTierAsync_SequentialTierChanges_AppliesLastTier()
    {
        // Arrange
        await UploadChunkAsync(ChunkHash1, 1024, StorageTier.Hot);

        // Act — chain of tier changes (Hot → Cool → Cold → Archive)
        await _blobService.SetBlobTierAsync($"chunks/{ChunkHash1}", StorageTier.Cool);
        await _blobService.SetBlobTierAsync($"chunks/{ChunkHash1}", StorageTier.Cold);
        await _blobService.SetBlobTierAsync($"chunks/{ChunkHash1}", StorageTier.Archive);

        // Assert — final tier wins
        var (_, tier) = await _blobService.GetBlobPropertiesAsync($"chunks/{ChunkHash1}");
        Assert.Equal(StorageTier.Archive, tier);
    }

    #endregion
}
