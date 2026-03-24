using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for corrupted file recovery paths across all restore methods.
/// Verifies that DataIntegrityException triggers corrupted recovery
/// (best-effort decryption to a __corrupted__ subfolder) consistently
/// across RestoreFilesWithRemappingAsync, RestoreFilesAsync, and MirrorSyncToLocalAsync.
/// </summary>
public class CorruptedRecoveryTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _restoreDirectory = null!;
    private string _dbPath = null!;

    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private LocalDatabaseService _databaseService = null!;

    private const string TestPassword = "TestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CorruptedRecoveryTests_{Guid.NewGuid():N}");
        _sourceDirectory = Path.Combine(_testDirectory, "source");
        _restoreDirectory = Path.Combine(_testDirectory, "restore");
        _dbPath = Path.Combine(_testDirectory, "test.db");

        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_restoreDirectory);

        _encryptionService = new EncryptionService();
        _chunkingService = new ChunkingService();
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestPassword);

        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);
        CryptographicOperations.ZeroMemory(key);
    }

    public Task DisposeAsync()
    {
        _encryptionService.Dispose();
        _databaseService.Dispose();

        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenChunkCorrupted_RecoveresToCorruptedSubfolder()
    {
        // Arrange
        CorruptOnDownloadBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "corrupted_remap.bin", 100 * 1024);
        blobService.CorruptDownloads = true;

        var targetPath = Path.Combine(_restoreDirectory, "corrupted_remap.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert
        Assert.Empty(result.SuccessfulFiles);
        Assert.Single(result.CorruptedRecoveryFiles);

        var (originalPath, recoveredPath, unrecoverableChunks) = result.CorruptedRecoveryFiles[0];
        Assert.Equal(backedUp.LocalPath, originalPath);
        Assert.Contains("__corrupted__", recoveredPath);
        Assert.True(File.Exists(recoveredPath), $"Recovered file should exist at {recoveredPath}");
        Assert.Equal(0, unrecoverableChunks);
    }

    [Fact]
    public async Task RestoreFilesAsync_WhenChunkCorrupted_RecoveresToCorruptedSubfolder()
    {
        // Arrange
        CorruptOnDownloadBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        await CreateAndBackupFile(blobService, "corrupted_batch.bin", 100 * 1024);
        blobService.CorruptDownloads = true;

        // Act
        var result = await restoreService.RestoreFilesAsync(
            await restoreService.ListRestorableFilesAsync(),
            _restoreDirectory,
            preserveFolderStructure: false);

        // Assert
        Assert.Empty(result.SuccessfulFiles);
        Assert.Single(result.CorruptedRecoveryFiles);

        var (_, recoveredPath, _) = result.CorruptedRecoveryFiles[0];
        Assert.Contains("__corrupted__", recoveredPath);
        Assert.True(File.Exists(recoveredPath));
    }

    [Fact]
    public async Task MirrorSyncToLocalAsync_WhenChunkCorrupted_ReportsCorruptedRecovery()
    {
        // Arrange
        CorruptOnDownloadBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "corrupted_sync.bin", 100 * 1024);
        blobService.CorruptDownloads = true;

        // Act
        var result = await restoreService.MirrorSyncToLocalAsync(
            [backedUp],
            _restoreDirectory,
            _sourceDirectory);

        // Assert
        Assert.Equal(0, result.FilesTransferred);
        Assert.Equal(1, result.FilesCorruptedRecovered);
        Assert.Single(result.CorruptedRecoveryPaths);
        Assert.Contains("__corrupted__", result.CorruptedRecoveryPaths[0]);
        Assert.True(File.Exists(result.CorruptedRecoveryPaths[0]),
            "Recovered file should survive mirror sync Phase 2 cleanup");
    }

    [Fact]
    public async Task CorruptedRecovery_WhenAllChunksUnrecoverable_EarlyBailoutReturnsFailure()
    {
        // Arrange — use a blob service that returns null for ALL best-effort downloads
        // This simulates wrong-key or completely destroyed data
        AllChunksUnrecoverableBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        // Create a file with multiple chunks (needs >=3 for the early bailout check)
        var backedUp = await CreateAndBackupFile(blobService, "unrecoverable.bin", 3 * 1024 * 1024);
        Assert.True(backedUp.Chunks.Count >= 3, 
            $"Need >=3 chunks to test early bailout, got {backedUp.Chunks.Count}");

        // Enable corruption for normal downloads (triggers DataIntegrityException)
        // and make best-effort return null (triggers early bailout)
        blobService.CorruptNormalDownloads = true;
        blobService.FailBestEffort = true;

        var targetPath = Path.Combine(_restoreDirectory, "unrecoverable.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — recovery should fail entirely (early bailout after 3 chunks)
        Assert.Empty(result.SuccessfulFiles);
        Assert.Empty(result.CorruptedRecoveryFiles);
        Assert.Single(result.FailedFiles);
    }

    [Fact]
    public async Task CorruptedRecovery_WithMixedChunks_ZeroFillsUnrecoverableChunks()
    {
        // Arrange — blob service where some chunks decrypt fine, others are completely unrecoverable
        PartialRecoveryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        // Create multi-chunk file
        var backedUp = await CreateAndBackupFile(blobService, "partial.bin", 3 * 1024 * 1024);
        Assert.True(backedUp.Chunks.Count >= 3,
            $"Need >=3 chunks to test partial recovery, got {backedUp.Chunks.Count}");

        // Enable corruption for normal downloads (triggers DataIntegrityException)
        // Best-effort will fail for chunk index 1 only (zero-filled)
        blobService.CorruptNormalDownloads = true;
        blobService.UnrecoverableChunkIndices.Add(1);

        var targetPath = Path.Combine(_restoreDirectory, "partial.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — file recovered to __corrupted__ with 1 zero-filled chunk
        Assert.Empty(result.SuccessfulFiles);
        Assert.Single(result.CorruptedRecoveryFiles);

        var (_, recoveredPath, unrecoverableChunks) = result.CorruptedRecoveryFiles[0];
        Assert.Equal(1, unrecoverableChunks);
        Assert.True(File.Exists(recoveredPath));

        // Verify recovered file size matches original (zero-filled chunks preserve size)
        var recoveredInfo = new FileInfo(recoveredPath);
        Assert.Equal(backedUp.FileSize, recoveredInfo.Length);
    }

    #region Helper Methods

    private static byte[] CreateRandomContent(int size)
    {
        byte[] content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    private async Task<BackedUpFile> CreateAndBackupFile(
        IBlobStorageService blobService, string relativePath, int size)
    {
        var fullPath = Path.Combine(_sourceDirectory, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var content = CreateRandomContent(size);
        await File.WriteAllBytesAsync(fullPath, content);

        FileInfo fileInfo = new(fullPath);
        var (chunks, _) = await _chunkingService.ChunkFileAsync(fullPath);
        var fileHash = await _chunkingService.ComputeFileHashAsync(fullPath);

        foreach (var chunk in chunks)
        {
            var chunkData = await _chunkingService.ReadChunkAsync(fullPath, chunk);
            chunk.BlobName = await blobService.UploadChunkAsync(chunkData, chunk.Hash);
        }

        BackedUpFile backedUp = new()
        {
            LocalPath = fullPath,
            BlobName = $"files/{Guid.NewGuid()}",
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc,
            FileHash = fileHash,
            Chunks = chunks,
            BackedUpAt = DateTime.UtcNow,
            Status = BackupStatus.Completed
        };

        await blobService.UploadFileMetadataAsync(backedUp);
        _databaseService.SaveBackedUpFile(backedUp);

        return backedUp;
    }

    #endregion
}

/// <summary>
/// Blob service that corrupts normal downloads (causing DataIntegrityException)
/// but allows best-effort downloads to succeed (CRC-only corruption).
/// This simulates the scenario where encrypted data has a corrupted CRC trailer.
/// </summary>
internal class CorruptOnDownloadBlobService : InMemoryBlobService
{
    public bool CorruptDownloads { get; set; }

    public CorruptOnDownloadBlobService(EncryptionService encryptionService)
        : base(encryptionService) { }

    public override async Task<byte[]> DownloadChunkAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        var data = await base.DownloadChunkAsync(blobName, cancellationToken);

        if (CorruptDownloads && data.Length > 0)
        {
            // Corrupt the decrypted data so SHA-256 verification fails in RestoreService,
            // which throws DataIntegrityException and triggers recovery
            var corrupted = data.ToArray();
            corrupted[0] ^= 0xFF;
            return corrupted;
        }

        return data;
    }

    // DownloadChunkBestEffortAsync inherits from InMemoryBlobService — returns good data
    // This simulates: CRC corrupted (normal decrypt fails validation) but AES-GCM tag OK
}

/// <summary>
/// Blob service where normal downloads are corrupted AND best-effort downloads
/// return null for all chunks. Simulates completely unrecoverable data (wrong key).
/// </summary>
internal class AllChunksUnrecoverableBlobService : InMemoryBlobService
{
    public bool CorruptNormalDownloads { get; set; }
    public bool FailBestEffort { get; set; }

    public AllChunksUnrecoverableBlobService(EncryptionService encryptionService)
        : base(encryptionService) { }

    public override async Task<byte[]> DownloadChunkAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        var data = await base.DownloadChunkAsync(blobName, cancellationToken);

        if (CorruptNormalDownloads && data.Length > 0)
        {
            var corrupted = data.ToArray();
            corrupted[0] ^= 0xFF;
            return corrupted;
        }

        return data;
    }

    public override Task<byte[]?> DownloadChunkBestEffortAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        if (FailBestEffort)
            return Task.FromResult<byte[]?>(null);

        return base.DownloadChunkBestEffortAsync(blobName, cancellationToken);
    }
}

/// <summary>
/// Blob service where normal downloads are corrupted and best-effort downloads
/// fail for specific chunk indices. Simulates partial recovery.
/// </summary>
internal class PartialRecoveryBlobService : InMemoryBlobService
{
    public bool CorruptNormalDownloads { get; set; }
    public HashSet<int> UnrecoverableChunkIndices { get; } = [];

    private int _bestEffortCallCount;

    public PartialRecoveryBlobService(EncryptionService encryptionService)
        : base(encryptionService) { }

    public override async Task<byte[]> DownloadChunkAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        var data = await base.DownloadChunkAsync(blobName, cancellationToken);

        if (CorruptNormalDownloads && data.Length > 0)
        {
            var corrupted = data.ToArray();
            corrupted[0] ^= 0xFF;
            return corrupted;
        }

        return data;
    }

    public override async Task<byte[]?> DownloadChunkBestEffortAsync(
        string blobName, CancellationToken cancellationToken = default)
    {
        var index = _bestEffortCallCount++;

        if (UnrecoverableChunkIndices.Contains(index))
            return null;

        return await base.DownloadChunkBestEffortAsync(blobName, cancellationToken);
    }
}
