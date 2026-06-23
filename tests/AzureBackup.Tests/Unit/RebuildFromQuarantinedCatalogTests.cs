using System.Security.Cryptography;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B51 end-to-end coverage for
/// <see cref="BackupOrchestrator.RebuildFromQuarantinedCatalogAsync"/>:
/// recover the in-database <c>PasswordSalt</c> from a quarantined
/// catalog and rebuild a fresh catalog at the active path from Azure
/// metadata, with no need for the user to recover the encrypted
/// connection string from the dead catalog.
/// </summary>
public class RebuildFromQuarantinedCatalogTests : IAsyncLifetime
{
    private const string TestPassword = "Quarantine-Rebuild-B51-Pwd!";
    private const string ConnectionString = "DefaultEndpointsProtocol=https;AccountName=test;AccountKey=AAAA==;EndpointSuffix=core.windows.net";
    private const string ContainerName = "rebuild-it";

    private string _testDir = null!;
    private string _dbPath = null!;

    private LocalDatabaseService _databaseService = null!;
    private EncryptionService _encryptionService = null!;
    private ChunkingService _chunkingService = null!;
    private InMemoryBlobService _blobService = null!;
    private FileWatcherService _fileWatcherService = null!;
    private ChunkIndexService _chunkIndexService = null!;
    private BackupOrchestrator _orchestrator = null!;

    public async Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-rebuild-q-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "active.db");

        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestPassword);
        _encryptionService = new EncryptionService();
        _chunkingService = new ChunkingService();
        _blobService = new InMemoryBlobService(_encryptionService);
        _fileWatcherService = new FileWatcherService(_databaseService);
        _orchestrator = new BackupOrchestrator(
            _databaseService,
            _encryptionService,
            _chunkingService,
            _blobService,
            _fileWatcherService);

        // First-run setup: this writes the original config (with a fresh
        // PasswordSalt) into the catalog and arms the encryption service.
        var initialized = await _orchestrator.InitializeAsync(TestPassword);
        Assert.True(initialized, "first-run InitializeAsync must succeed");

        await _orchestrator.SaveConnectionStringAsync(ConnectionString, ContainerName);

        _chunkIndexService = new ChunkIndexService(_databaseService, _blobService, _encryptionService);
        _orchestrator.SetChunkIndexService(_chunkIndexService);
    }

    public async Task DisposeAsync()
    {
        await _orchestrator.DisposeAsync();
        await _blobService.DisposeAsync();
        _fileWatcherService.Dispose();
        _encryptionService.Dispose();
        _databaseService.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task RebuildFromQuarantinedCatalog_RestoresFilesFromAzure_AndPreservesPasswordSalt()
    {
        // Arrange: seed Azure with two complete files using the live
        // encryption key (so the rebuild can decrypt the metadata blobs
        // with the recovered key).
        var fileA = await SeedFileAsync(@"C:\Backup\alpha.txt", chunkCount: 2);
        var fileB = await SeedFileAsync(@"C:\Backup\beta.bin", chunkCount: 1);

        // Capture the current PasswordSalt from the catalog so we can
        // assert the rebuild recovers the SAME salt -- a different value
        // would mean the new catalog cannot decrypt the existing Azure
        // blobs and the recovery is silently broken.
        var originalSalt = _databaseService.GetConfiguration().PasswordSalt;
        Assert.NotNull(originalSalt);

        // Act 1: quarantine the active catalog. This closes the live
        // connection and moves the AZDB snapshot to the
        // .quarantine-yyyyMMdd-HHmmss suffix. The snapshot format has NO
        // .salt sidecar (the salt is embedded in the envelope).
        var quarantine = await _orchestrator.QuarantineCorruptCatalogAsync(_dbPath);
        var quarantinedDbPath = quarantine.QuarantinedDatabasePath;
        Assert.True(File.Exists(quarantinedDbPath));
        Assert.False(File.Exists(_dbPath), "active catalog path must be free after quarantine");

        // Act 2: rebuild a fresh catalog at the active path using the
        // user-supplied connection details and the quarantined snapshot.
        await _orchestrator.RebuildFromQuarantinedCatalogAsync(
            quarantinedDbPath,
            TestPassword.AsMemory(),
            ConnectionString,
            ContainerName,
            _dbPath);

        // Assert: a fresh catalog now exists at the active path with the
        // recovered PasswordSalt, and the Azure rebuild repopulated the
        // BackedUpFile graph.
        Assert.True(File.Exists(_dbPath), "fresh catalog must exist at the active path");

        var rebuiltConfig = _databaseService.GetConfiguration();
        Assert.NotNull(rebuiltConfig.PasswordSalt);
        Assert.Equal(originalSalt, rebuiltConfig.PasswordSalt);
        Assert.Equal(ContainerName, rebuiltConfig.ContainerName);

        var rebuiltFiles = _databaseService.GetAllBackedUpFiles()
            .OrderBy(f => f.LocalPath, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, rebuiltFiles.Count);

        var restoredA = _databaseService.GetBackedUpFile(fileA.LocalPath);
        Assert.NotNull(restoredA);
        Assert.Equal(fileA.FileSize, restoredA!.FileSize);
        Assert.Equal(fileA.Chunks.Count, restoredA.Chunks.Count);

        var restoredB = _databaseService.GetBackedUpFile(fileB.LocalPath);
        Assert.NotNull(restoredB);
        Assert.Single(restoredB!.Chunks);
    }

    [Fact]
    public async Task RebuildFromQuarantinedCatalog_WrongPassword_ThrowsInvalidPassword_LeavesActivePathFree()
    {
        // The rebuild must NOT initialise a fresh catalog at the active
        // path when the password is wrong -- otherwise a typo would
        // create a fresh catalog that the user could unlock with the
        // typo, masking the recovery failure entirely.
        await SeedFileAsync(@"C:\Backup\should-not-rebuild.txt", chunkCount: 1);

        var quarantine = await _orchestrator.QuarantineCorruptCatalogAsync(_dbPath);

        await Assert.ThrowsAsync<InvalidPasswordException>(() =>
            _orchestrator.RebuildFromQuarantinedCatalogAsync(
                quarantine.QuarantinedDatabasePath,
                "Definitely-Not-The-Password!".AsMemory(),
                ConnectionString,
                ContainerName,
                _dbPath));

        Assert.False(File.Exists(_dbPath),
            "active catalog path must remain untouched after a wrong-password rebuild");
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
                BlobName = $"chunks/{hash}",
            });
            offset += data.Length;
        }

        var file = new BackedUpFile
        {
            LocalPath = localPath,
            FileSize = offset,
            LastModified = DateTime.UtcNow,
            FileHash = Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(localPath))).ToLowerInvariant(),
            Status = BackupStatus.Completed,
            BackedUpAt = DateTime.UtcNow,
            MetadataVersion = 1,
            Chunks = chunks,
        };
        await _blobService.UploadFileMetadataAsync(file);
        return file;
    }
}
