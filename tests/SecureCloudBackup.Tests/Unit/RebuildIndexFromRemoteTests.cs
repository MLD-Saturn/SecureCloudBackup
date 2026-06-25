using System.Security.Cryptography;
using SecureCloudBackup.Core.Models;
using SecureCloudBackup.Core.Services;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// B46 regression: Rebuild from Azure must repopulate the backed-up-file
/// catalog (<c>files</c> + <c>file_chunks</c>) in addition to the chunk
/// index and reverse index. Pre-B46 the rebuild only restored the chunk
/// index; the Files catalog was left empty, breaking any downstream
/// consumer that called <c>GetBackedUpFile</c> / <c>GetAllBackedUpFiles</c>
/// after a recovery (Sync, Data Integrity, etc.).
/// </summary>
public class RebuildIndexFromRemoteTests : IAsyncLifetime
{
    private const string TestPassword = "TestPassword123!";
    private string _testDbPath = null!;
    private LocalDatabaseService _databaseService = null!;
    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;
    private ChunkIndexService _indexService = null!;

    public async Task InitializeAsync()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"B46Rebuild_{Guid.NewGuid()}.db");

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
    public async Task RebuildIndexFromRemoteAsync_RepopulatesFilesAndChunksFromAzureMetadata()
    {
        // Arrange: seed Azure with two complete files, each referencing
        // its own chunk(s). Then wipe the local catalog and rebuild.
        var fileA = await SeedFileAsync(@"C:\Backup\alpha.txt", chunkCount: 2);
        var fileB = await SeedFileAsync(@"C:\Backup\beta.bin", chunkCount: 1);

        _databaseService.ClearBackedUpFiles();
        _databaseService.ClearChunkIndex();
        Assert.Empty(_databaseService.GetAllBackedUpFiles());

        // Act
        await _indexService.RebuildIndexFromRemoteAsync();

        // Assert: chunk index restored.
        Assert.Equal(3, _databaseService.GetChunkIndexCount());

        // Assert: Files catalog restored with full chunk graph (B46 fix).
        var allFiles = _databaseService.GetAllBackedUpFiles()
            .OrderBy(f => f.LocalPath, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, allFiles.Count);

        var restoredA = _databaseService.GetBackedUpFile(fileA.LocalPath);
        Assert.NotNull(restoredA);
        Assert.Equal(fileA.FileSize, restoredA!.FileSize);
        Assert.Equal(fileA.Chunks.Count, restoredA.Chunks.Count);
        Assert.Equal(
            fileA.Chunks.Select(c => c.Hash).ToList(),
            restoredA.Chunks.OrderBy(c => c.Index).Select(c => c.Hash).ToList());

        var restoredB = _databaseService.GetBackedUpFile(fileB.LocalPath);
        Assert.NotNull(restoredB);
        Assert.Single(restoredB!.Chunks);
        Assert.Equal(fileB.Chunks[0].Hash, restoredB.Chunks[0].Hash);
    }

    [Fact]
    public async Task RebuildIndexFromRemoteAsync_OmitsFilesWithMissingChunks()
    {
        // Arrange: a complete file plus a metadata-only entry whose chunk
        // is missing from Azure. The rebuild deletes the incomplete
        // metadata blob (and any orphaned chunks) and must NOT carry the
        // incomplete file into the rebuilt Files catalog.
        var goodFile = await SeedFileAsync(@"C:\Backup\good.txt", chunkCount: 1);
        await SeedMetadataOnlyAsync(@"C:\Backup\incomplete.txt", chunkCount: 1);

        _databaseService.ClearBackedUpFiles();
        _databaseService.ClearChunkIndex();

        // Act
        await _indexService.RebuildIndexFromRemoteAsync();

        // Assert
        var files = _databaseService.GetAllBackedUpFiles();
        Assert.Single(files);
        Assert.Equal(goodFile.LocalPath, files[0].LocalPath);
        Assert.Null(_databaseService.GetBackedUpFile(@"C:\Backup\incomplete.txt"));
    }

    [Fact]
    public async Task RebuildIndexFromRemoteAsync_DiscardsStaleFilesNotInAzure()
    {
        // Arrange: Azure has one file. The local catalog has a stale row
        // for a path whose Azure metadata has since been deleted. After
        // rebuild only the Azure-backed file should remain.
        var azureFile = await SeedFileAsync(@"C:\Backup\current.txt", chunkCount: 1);

        var staleFile = new BackedUpFile
        {
            LocalPath = @"C:\Backup\deleted-from-azure.txt",
            FileSize = 42,
            LastModified = DateTime.UtcNow,
            FileHash = "stalehash",
            Status = BackupStatus.Completed,
            BackedUpAt = DateTime.UtcNow,
            MetadataVersion = 1,
            Chunks = new List<ChunkInfo>
            {
                new() { Index = 0, Hash = "stalechunkhash", Offset = 0, Length = 42, BlobName = "chunks/stalechunkhash" }
            }
        };
        _databaseService.SaveBackedUpFile(staleFile);

        // Act
        await _indexService.RebuildIndexFromRemoteAsync();

        // Assert
        var files = _databaseService.GetAllBackedUpFiles();
        Assert.Single(files);
        Assert.Equal(azureFile.LocalPath, files[0].LocalPath);
        Assert.Null(_databaseService.GetBackedUpFile(staleFile.LocalPath));
    }

    private async Task<BackedUpFile> SeedFileAsync(string localPath, int chunkCount)
    {
        var chunks = new List<ChunkInfo>(chunkCount);
        long offset = 0;
        for (var i = 0; i < chunkCount; i++)
        {
            var data = new byte[256];
            Random.Shared.NextBytes(data);
            var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            await _blobService.UploadChunkAsync(data, hash, StorageTier.Hot);
            chunks.Add(new ChunkInfo
            {
                Index = i,
                Hash = hash,
                Offset = offset,
                Length = data.Length,
                BlobName = $"chunks/{hash}"
            });
            offset += data.Length;
        }

        var file = new BackedUpFile
        {
            LocalPath = localPath,
            FileSize = offset,
            LastModified = DateTime.UtcNow,
            FileHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(localPath))).ToLowerInvariant(),
            Status = BackupStatus.Completed,
            BackedUpAt = DateTime.UtcNow,
            MetadataVersion = 1,
            Chunks = chunks
        };
        await _blobService.UploadFileMetadataAsync(file);
        return file;
    }

    private async Task SeedMetadataOnlyAsync(string localPath, int chunkCount)
    {
        var chunks = new List<ChunkInfo>(chunkCount);
        long offset = 0;
        for (var i = 0; i < chunkCount; i++)
        {
            // Hash references a chunk that is NOT uploaded - rebuild
            // must treat this metadata blob as incomplete.
            var hash = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{localPath}:{i}:missing"))).ToLowerInvariant();
            chunks.Add(new ChunkInfo
            {
                Index = i,
                Hash = hash,
                Offset = offset,
                Length = 256,
                BlobName = $"chunks/{hash}"
            });
            offset += 256;
        }

        var file = new BackedUpFile
        {
            LocalPath = localPath,
            FileSize = offset,
            LastModified = DateTime.UtcNow,
            FileHash = "missingchunkhash",
            Status = BackupStatus.Completed,
            BackedUpAt = DateTime.UtcNow,
            MetadataVersion = 1,
            Chunks = chunks
        };
        await _blobService.UploadFileMetadataAsync(file);
    }
}
