using System.Collections.Concurrent;
using System.Security.Cryptography;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Comprehensive unit tests for BackupOrchestrator covering initialization, 
/// backup operations, parallel upload logic, and coordination between services.
/// </summary>
public class BackupOrchestratorTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _sourceDirectory = null!;
    private string _dbPath = null!;
    
    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;
    private LocalDatabaseService _databaseService = null!;
    private FileWatcherService _fileWatcherService = null!;
    private BackupOrchestrator _orchestrator = null!;

    private const string TestPassword = "OrchestratorTestPassword123!";

    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"OrchestratorTests_{Guid.NewGuid():N}");
        _sourceDirectory = Path.Combine(_testDirectory, "source");
        _dbPath = Path.Combine(_testDirectory, "test.db");
        
        Directory.CreateDirectory(_sourceDirectory);
        
        _encryptionService = new EncryptionService();
        _blobService = new InMemoryBlobService(_encryptionService);
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath);
        _fileWatcherService = new FileWatcherService(_databaseService);
        
        // Create orchestrator with IBlobStorageService (InMemoryBlobService implements this)
        _orchestrator = new BackupOrchestrator(
            _databaseService,
            _encryptionService,
            new ChunkingService(),
            _blobService,
            _fileWatcherService);
        
        await _blobService.ConnectAsync("fake-connection-string", "test-container");
        
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _orchestrator.DisposeAsync();
        _encryptionService.Dispose();
        _databaseService.Dispose();
        
        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    #region Initialization Tests

    [Fact]
    public async Task InitializeAsync_WithValidPassword_Succeeds()
    {
        // Act
        var result = await _orchestrator.InitializeAsync(TestPassword);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task InitializeAsync_WithEmptyPassword_ThrowsArgumentException()
    {
        // Act & Assert - Empty password throws ArgumentException (not returns false)
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _orchestrator.InitializeAsync(""));
    }

    [Fact]
    public async Task InitializeAsync_WithShortPassword_ThrowsSecurityPolicyException()
    {
        // Act & Assert - Password too short (< 12 chars)
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            _orchestrator.InitializeAsync("Short1!"));
    }

    [Fact]
    public async Task InitializeAsync_WithWeakPassword_OnlyOneCharType_ThrowsSecurityPolicyException()
    {
        // Act & Assert - password with only lowercase (1 of 4 types, needs 3)
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            _orchestrator.InitializeAsync("onlylowercaseletters"));
    }
    
    [Fact]
    public async Task InitializeAsync_WithWeakPassword_TwoCharTypes_ThrowsSecurityPolicyException()
    {
        // Act & Assert - password with lowercase and digits only (2 of 4 types, needs 3)
        await Assert.ThrowsAsync<SecurityPolicyException>(() =>
            _orchestrator.InitializeAsync("lowercase123456"));
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_SecondCallWithSamePassword_Succeeds()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);

        // Act - Initialize again with same password
        var result = await _orchestrator.InitializeAsync(TestPassword);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task InitializeAsync_CalledTwice_SecondCallWithWrongPassword_ReturnsFalse()
    {
        // Arrange - First initialization sets the password
        await _orchestrator.InitializeAsync(TestPassword);

        // Act - Try to initialize with wrong password
        var result = await _orchestrator.InitializeAsync("WrongPassword123!");

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Backup File Tests

    [Fact]
    public async Task BackupFileAsync_SingleSmallFile_BacksUpSuccessfully()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(50 * 1024); // 50 KB
        var filePath = Path.Combine(_sourceDirectory, "small_file.txt");
        await File.WriteAllBytesAsync(filePath, content);

        // Act
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        
        // Verify file is in database
        var backedUp = _databaseService.GetBackedUpFile(filePath);
        Assert.NotNull(backedUp);
        Assert.Equal(BackupStatus.Completed, backedUp.Status);
    }

    [Fact]
    public async Task BackupFileAsync_LargeFile_CreatesMultipleChunks()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(3 * 1024 * 1024); // 3 MB
        var filePath = Path.Combine(_sourceDirectory, "large_file.bin");
        await File.WriteAllBytesAsync(filePath, content);

        // Act
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        
        var backedUp = _databaseService.GetBackedUpFile(filePath);
        Assert.NotNull(backedUp);
        Assert.True(backedUp.Chunks.Count > 1, "Large file should have multiple chunks");
    }

    [Fact]
    public async Task BackupFileAsync_NonExistentFile_ReturnsTrue_HandlesGracefully()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        var filePath = Path.Combine(_sourceDirectory, "nonexistent.txt");

        // Act
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert - Non-existent files return true (graceful handling of deleted files)
        // The orchestrator marks them as Excluded if they had a previous record
        Assert.True(result);
    }

    [Fact]
    public async Task BackupFileAsync_SameFileUnchanged_SkipsUpload()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(100 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "unchanged.txt");
        await File.WriteAllBytesAsync(filePath, content);

        // First backup
        await _orchestrator.BackupFileAsync(filePath);
        var initialOperations = _blobService.TotalOperations;

        // Act - Backup same file again without changes
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        // No additional chunk upload operations should occur (only metadata check)
        Assert.Equal(initialOperations, _blobService.TotalOperations);
    }

    [Fact]
    public async Task BackupFileAsync_ModifiedFile_UploadsNewChunks()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(100 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "modified.txt");
        await File.WriteAllBytesAsync(filePath, content);

        // First backup
        await _orchestrator.BackupFileAsync(filePath);
        var initialHash = _databaseService.GetBackedUpFile(filePath)?.FileHash;

        // Modify the file
        var newContent = CreateRandomContent(100 * 1024);
        await File.WriteAllBytesAsync(filePath, newContent);

        // Act - Backup modified file
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        var newHash = _databaseService.GetBackedUpFile(filePath)?.FileHash;
        Assert.NotEqual(initialHash, newHash);
    }

    [Fact]
    public async Task BackupFileAsync_WithCancellation_StopsGracefully()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(5 * 1024 * 1024); // 5 MB
        var filePath = Path.Combine(_sourceDirectory, "large_cancel.bin");
        await File.WriteAllBytesAsync(filePath, content);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // Act & Assert - Should not throw unhandled exception
        try
        {
            await _orchestrator.BackupFileAsync(filePath, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    #endregion

    #region Progress Reporting Tests

    [Fact]
    public async Task BackupFileAsync_WithProgress_ReportsProgress()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(200 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "progress_test.txt");
        await File.WriteAllBytesAsync(filePath, content);

        var progressReports = new ConcurrentBag<(long current, long total)>();
        var progress = new Progress<(long current, long total)>(p => progressReports.Add(p));

        // Act
        await _orchestrator.BackupFileAsync(filePath, progress);

        // Assert
        Assert.NotEmpty(progressReports);
        
        // Final progress should reach or exceed total (encryption adds overhead)
        var reports = progressReports.ToList();
        var maxProgress = reports.Max(p => p.current);
        var anyTotal = reports.First().total;
        Assert.True(maxProgress >= anyTotal, 
            $"Final progress ({maxProgress}) should reach total ({anyTotal})");
    }

    [Fact]
    public async Task BackupFileAsync_ProgressEvents_Fired()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(100 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "event_test.txt");
        await File.WriteAllBytesAsync(filePath, content);

        var progressEventFired = false;
        _orchestrator.ProgressChanged += (_, _) => progressEventFired = true;

        // Act
        await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(progressEventFired);
    }

    [Fact]
    public async Task BackupFileAsync_StatusChanged_EventFired()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(50 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "status_test.txt");
        await File.WriteAllBytesAsync(filePath, content);

        var statusMessages = new ConcurrentBag<string>();
        _orchestrator.StatusChanged += (_, msg) => statusMessages.Add(msg);

        // Act
        await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.NotEmpty(statusMessages);
        Assert.Contains(statusMessages, m => m.Contains("Backing up"));
    }

    #endregion

    #region Deduplication Tests

    [Fact]
    public async Task BackupFileAsync_DuplicateContent_DeduplicatesChunks()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(100 * 1024);
        var file1 = Path.Combine(_sourceDirectory, "file1.txt");
        var file2 = Path.Combine(_sourceDirectory, "file2.txt");
        
        // Write identical content to both files
        await File.WriteAllBytesAsync(file1, content);
        await File.WriteAllBytesAsync(file2, content);

        // Act - Backup both files
        await _orchestrator.BackupFileAsync(file1);
        var storageAfterFirst = _blobService.TotalStorageUsed;
        
        await _orchestrator.BackupFileAsync(file2);
        var storageAfterSecond = _blobService.TotalStorageUsed;

        // Assert - Storage should not double (deduplication)
        // Metadata will be different, but chunks should be shared
        Assert.True(storageAfterSecond < storageAfterFirst * 2,
            $"Deduplication failed: storage doubled from {storageAfterFirst} to {storageAfterSecond}");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task BackupFileAsync_EmptyFile_HandlesCorrectly()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var filePath = Path.Combine(_sourceDirectory, "empty.txt");
        await File.WriteAllBytesAsync(filePath, []);

        // Act
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        
        var backedUp = _databaseService.GetBackedUpFile(filePath);
        Assert.NotNull(backedUp);
        Assert.Equal(0, backedUp.FileSize);
    }

    [Fact]
    public async Task BackupFileAsync_FileWithSpecialCharacters_HandlesCorrectly()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var content = CreateRandomContent(10 * 1024);
        var filePath = Path.Combine(_sourceDirectory, "file with spaces & symbols (1).txt");
        await File.WriteAllBytesAsync(filePath, content);

        // Act
        var result = await _orchestrator.BackupFileAsync(filePath);

        // Assert
        Assert.True(result);
        
        var backedUp = _databaseService.GetBackedUpFile(filePath);
        Assert.NotNull(backedUp);
    }

    #endregion

    #region State Management Tests

    [Fact]
    public void IsRunning_InitiallyFalse()
    {
        Assert.False(_orchestrator.IsRunning);
    }

    [Fact]
    public void IsPaused_InitiallyFalse()
    {
        Assert.False(_orchestrator.IsPaused);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task BackupFileAsync_ConcurrentBackups_ThreadSafe()
    {
        // Arrange
        await _orchestrator.InitializeAsync(TestPassword);
        
        var files = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var content = CreateRandomContent(50 * 1024);
            var filePath = Path.Combine(_sourceDirectory, $"concurrent_{i}.txt");
            await File.WriteAllBytesAsync(filePath, content);
            files.Add(filePath);
        }

        // Act - Backup files concurrently
        var tasks = files.Select(f => _orchestrator.BackupFileAsync(f)).ToList();
        var results = await Task.WhenAll(tasks);

        // Assert - All backups should succeed
        Assert.All(results, r => Assert.True(r));
        
        // All files should be in database
        foreach (var file in files)
        {
            var backedUp = _databaseService.GetBackedUpFile(file);
            Assert.NotNull(backedUp);
        }
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateRandomContent(int size)
    {
        var content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    #endregion
}
