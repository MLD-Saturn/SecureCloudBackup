using System.Security.Cryptography;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for resilience, failure recovery, and edge cases in backup/restore operations.
/// Uses InMemoryBlobService with simulated failures to test error handling.
/// </summary>
public class BackupRestoreResilienceTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _restoreDirectory = null!;
    private string _dbPath = null!;
    
    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private LocalDatabaseService _databaseService = null!;

    private const string TestPassword = "ResilienceTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"ResilienceTests_{Guid.NewGuid():N}");
        _sourceDirectory = Path.Combine(_testDirectory, "source");
        _restoreDirectory = Path.Combine(_testDirectory, "restore");
        _dbPath = Path.Combine(_testDirectory, "test.db");
        
        Directory.CreateDirectory(_sourceDirectory);
        Directory.CreateDirectory(_restoreDirectory);
        
        _encryptionService = new EncryptionService();
        _chunkingService = new ChunkingService();
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath);
        
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

    #region Connection Failure Tests

    [Fact]
    public async Task BlobService_NotConnected_ThrowsOnUpload()
    {
        // Arrange - Create blob service but don't connect
        var blobService = new InMemoryBlobService(_encryptionService);
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            blobService.UploadChunkAsync(data, hash));
    }

    [Fact]
    public async Task BlobService_NotConnected_ThrowsOnDownload()
    {
        // Arrange - Create blob service but don't connect
        var blobService = new InMemoryBlobService(_encryptionService);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            blobService.DownloadChunkAsync("chunks/somehash"));
    }

    #endregion

    #region Simulated Latency Tests

    [Fact]
    public async Task BlobService_WithLatency_StillCompletes()
    {
        // Arrange - Create blob service with 50ms simulated latency
        var blobService = new InMemoryBlobService(_encryptionService, simulatedLatencyMs: 50);
        await blobService.ConnectAsync("fake-connection", "container");
        
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var blobName = await blobService.UploadChunkAsync(data, hash);
        var downloaded = await blobService.DownloadChunkAsync(blobName);

        stopwatch.Stop();

        // Assert
        Assert.Equal(data, downloaded);
        // Should have taken at least 100ms (50ms upload + 50ms download)
        Assert.True(stopwatch.ElapsedMilliseconds >= 80); // Allow some tolerance
    }

    #endregion

    #region Restore Edge Cases

    [Fact]
    public async Task RestoreService_FileAlreadyExists_NoOverwrite_ReturnsFalse()
    {
        // Arrange
        var blobService = new InMemoryBlobService(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        var restoreService = new RestoreService(_databaseService, blobService, _encryptionService);

        // Create and backup a file
        var content = CreateRandomContent(10 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "existing.txt");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(blobService, sourceFile);

        // Create a file at the restore location
        var restorePath = Path.Combine(_restoreDirectory, "existing.txt");
        await File.WriteAllBytesAsync(restorePath, new byte[] { 1, 2, 3 });

        // Act - Try to restore without overwrite
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, overwriteExisting: false);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task RestoreService_FileAlreadyExists_WithOverwrite_Succeeds()
    {
        // Arrange
        var blobService = new InMemoryBlobService(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        var restoreService = new RestoreService(_databaseService, blobService, _encryptionService);

        var content = CreateRandomContent(10 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "overwrite.txt");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(blobService, sourceFile);

        // Create a different file at the restore location
        var restorePath = Path.Combine(_restoreDirectory, "overwrite.txt");
        await File.WriteAllBytesAsync(restorePath, new byte[] { 1, 2, 3 });

        // Act - Restore with overwrite
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, overwriteExisting: true);

        // Assert
        Assert.True(result);
        var restoredContent = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(content, restoredContent);
    }

    [Fact]
    public async Task RestoreService_ToNestedDirectory_CreatesDirectories()
    {
        // Arrange
        var blobService = new InMemoryBlobService(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        var restoreService = new RestoreService(_databaseService, blobService, _encryptionService);

        var content = CreateRandomContent(10 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "nested.txt");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(blobService, sourceFile);

        // Restore to a deeply nested path that doesn't exist
        var restorePath = Path.Combine(_restoreDirectory, "level1", "level2", "level3", "nested.txt");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, overwriteExisting: true);

        // Assert
        Assert.True(result);
        Assert.True(File.Exists(restorePath));
    }

    #endregion

    #region Delete Operation Tests

    [Fact]
    public async Task DeleteFileAsync_RemovesMetadata()
    {
        // Arrange
        var blobService = new InMemoryBlobService(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        var restoreService = new RestoreService(_databaseService, blobService, _encryptionService);

        var content = CreateRandomContent(50 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "to_delete.txt");
        await File.WriteAllBytesAsync(sourceFile, content);

        var backedUp = await BackupFileAsync(blobService, sourceFile);

        // Verify file exists
        var filesBefore = await restoreService.ListRestorableFilesAsync();
        Assert.Single(filesBefore);

        // Act
        var result = await restoreService.DeleteFileAsync(backedUp);

        // Assert
        Assert.True(result);
        var filesAfter = await restoreService.ListRestorableFilesAsync();
        Assert.Empty(filesAfter);
    }

    #endregion

    #region Empty and Boundary Tests

    [Fact]
    public async Task BackupAndRestore_EmptyFile_Succeeds()
    {
        // Arrange
        var blobService = new InMemoryBlobService(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        var restoreService = new RestoreService(_databaseService, blobService, _encryptionService);

        var sourceFile = Path.Combine(_sourceDirectory, "empty.txt");
        await File.WriteAllBytesAsync(sourceFile, []);
        
        var backedUp = await BackupFileAsync(blobService, sourceFile);
        var restorePath = Path.Combine(_restoreDirectory, "empty.txt");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, overwriteExisting: true);

        // Assert
        Assert.True(result);
        Assert.True(File.Exists(restorePath));
        Assert.Equal(0, new FileInfo(restorePath).Length);
    }

    [Fact]
    public async Task BackupAndRestore_ExactlyMinChunkSize_Succeeds()
    {
        // Arrange
        var blobService = new InMemoryBlobService(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        var restoreService = new RestoreService(_databaseService, blobService, _encryptionService);

        // Create file exactly at minimum chunk size boundary (64 KB)
        var content = CreateRandomContent(64 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "boundary.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(blobService, sourceFile);
        var restorePath = Path.Combine(_restoreDirectory, "boundary.bin");

        // Act
        var result = await restoreService.RestoreFileAsync(backedUp, restorePath, overwriteExisting: true);

        // Assert
        Assert.True(result);
        var restored = await File.ReadAllBytesAsync(restorePath);
        Assert.Equal(content, restored);
    }

    [Fact]
    public async Task BackupAndRestore_OneByteLessThanMinChunk_SingleChunk()
    {
        // Arrange
        var blobService = new InMemoryBlobService(_encryptionService);
        await blobService.ConnectAsync("fake", "container");

        var content = CreateRandomContent(64 * 1024 - 1); // One byte less than min
        var sourceFile = Path.Combine(_sourceDirectory, "small.bin");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(blobService, sourceFile);

        // Assert - Should be single chunk
        Assert.Single(backedUp.Chunks);
    }

    #endregion

    #region Progress Reporting Tests

    [Fact]
    public async Task RestoreFile_ReportsProgress()
    {
        // Arrange
        var blobService = new InMemoryBlobService(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        var restoreService = new RestoreService(_databaseService, blobService, _encryptionService);

        var content = CreateRandomContent(200 * 1024);
        var sourceFile = Path.Combine(_sourceDirectory, "progress.txt");
        await File.WriteAllBytesAsync(sourceFile, content);
        
        var backedUp = await BackupFileAsync(blobService, sourceFile);
        var restorePath = Path.Combine(_restoreDirectory, "progress.txt");

        var progressReports = new List<(long current, long total)>();
        var progress = new Progress<(long current, long total)>(p => progressReports.Add(p));

        // Act
        await restoreService.RestoreFileAsync(backedUp, restorePath, true, progress);

        // Assert
        Assert.NotEmpty(progressReports);
        
        // Final report should show completion
        var last = progressReports.Last();
        Assert.Equal(last.total, last.current);
    }

    #endregion

    #region Helper Methods

    private async Task<BackedUpFile> BackupFileAsync(IBlobStorageService blobService, string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var chunks = await _chunkingService.ChunkFileAsync(filePath);
        var fileHash = await _chunkingService.ComputeFileHashAsync(filePath);

        foreach (var chunk in chunks)
        {
            var chunkData = await _chunkingService.ReadChunkAsync(filePath, chunk);
            chunk.BlobName = await blobService.UploadChunkAsync(chunkData, chunk.Hash);
        }

        var backedUp = new BackedUpFile
        {
            LocalPath = filePath,
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

    private static byte[] CreateRandomContent(int size)
    {
        var content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    private static string ComputeHash(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    #endregion
}
