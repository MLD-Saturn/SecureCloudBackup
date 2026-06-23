using AzureBackup.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using AzureBackup.Core;
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

        // Assert — CRC-only corruption: all chunks recoverable, so the file is
        // promoted from __corrupted__ to the original target path and counted as success
        Assert.Single(result.SuccessfulFiles);
        Assert.Empty(result.CorruptedRecoveryFiles);
        Assert.Equal(targetPath, result.SuccessfulFiles[0]);
        Assert.True(File.Exists(targetPath), $"Promoted file should exist at {targetPath}");
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenCrcOnlyCorruption_AutoRepairsChunksInStorage()
    {
        // Arrange
        CorruptOnDownloadBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "selfheal.bin", 100 * 1024);
        blobService.CorruptDownloads = true;

        var targetPath = Path.Combine(_restoreDirectory, "selfheal.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — every chunk decrypted (CRC-only), so each one is repaired in place
        Assert.Single(result.SuccessfulFiles);
        Assert.Equal(backedUp.Chunks.Count, blobService.RepairedCount);
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenChunkUnrecoverable_DoesNotRepairInStorage()
    {
        // Arrange — best-effort returns null for all chunks, so recovery zero-fills
        // and the file is NOT fully recoverable; no repair must be attempted.
        AllChunksUnrecoverableBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "noheal.bin", 100 * 1024);
        blobService.CorruptNormalDownloads = true;
        blobService.FailBestEffort = true;

        var targetPath = Path.Combine(_restoreDirectory, "noheal.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — unrecoverable data is reported as a failure and never repaired
        Assert.Equal(0, blobService.RepairCount);
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

        // Act — use RestoreFilesWithRemappingAsync (RestoreFilesAsync was removed)
        var files = await restoreService.ListRestorableFilesAsync();
        var filesWithPaths = files.Select(f => (f, Path.Combine(_restoreDirectory, Path.GetFileName(f.LocalPath)))).ToList();
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — CRC-only corruption: all chunks recoverable, so the file is
        // promoted from __corrupted__ to the original target path and counted as success
        Assert.Single(result.SuccessfulFiles);
        Assert.Empty(result.CorruptedRecoveryFiles);
        Assert.True(File.Exists(result.SuccessfulFiles[0]));
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

        // Assert — CRC-only corruption: all chunks recoverable, so the file is
        // promoted from __corrupted__ to the original target path and counted as transferred
        Assert.Equal(1, result.FilesTransferred);
        Assert.Equal(0, result.FilesCorruptedRecovered);
        Assert.Empty(result.CorruptedRecoveryPaths);
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

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenMd5MismatchTransient_RetriesAndSucceeds()
    {
        // Arrange — every chunk download reports an MD5 mismatch the first 5 times, then
        // succeeds. The restore must re-download the chunk (up to MaxIntegrityRetries) and
        // restore normally WITHOUT falling back to the best-effort recovery path.
        Md5RetryBlobService blobService = new(_encryptionService, throwsPerBlob: 5);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "md5_retry.bin", 100 * 1024);
        blobService.FailuresEnabled = true;

        var targetPath = Path.Combine(_restoreDirectory, "md5_retry.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — restored via retries; recovery (best-effort) never invoked.
        Assert.Single(result.SuccessfulFiles);
        Assert.Empty(result.CorruptedRecoveryFiles);
        Assert.Equal(0, blobService.BestEffortCalls);
        // Each chunk: 5 failed downloads + 1 success = 6 attempts.
        Assert.All(backedUp.Chunks, c => Assert.Equal(6, blobService.AttemptsFor(c.BlobName)));
        Assert.True(File.Exists(targetPath));
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenMd5MismatchPersistent_ExhaustsFiveRetriesThenRecovers()
    {
        // Arrange — every chunk download always reports an MD5 mismatch. After exhausting the
        // 5 retries (6 total download attempts) the file falls back to best-effort recovery,
        // which restores the (intact) data.
        Md5RetryBlobService blobService = new(_encryptionService, throwsPerBlob: int.MaxValue);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "md5_persistent.bin", 100 * 1024);
        blobService.FailuresEnabled = true;

        var targetPath = Path.Combine(_restoreDirectory, "md5_persistent.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — at least one chunk was re-downloaded the full 6 times (1 initial + 5 retries)
        // before recovery took over, and best-effort recovery then restored the data.
        Assert.Equal(6, backedUp.Chunks.Max(c => blobService.AttemptsFor(c.BlobName)));
        Assert.True(blobService.BestEffortCalls > 0, "Recovery (best-effort) should run after the 5 MD5 retries are exhausted");
        Assert.Single(result.SuccessfulFiles);
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_DuringRecovery_ReportsRecoveringProgressToCompletion()
    {
        // Arrange — multi-chunk file whose normal download is corrupted (post-decrypt hash
        // mismatch) so it drops into recovery; chunk index 1 is unrecoverable (zero-filled).
        PartialRecoveryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "recover_progress.bin", 3 * 1024 * 1024);
        Assert.True(backedUp.Chunks.Count >= 3, $"Need >=3 chunks, got {backedUp.Chunks.Count}");
        blobService.CorruptNormalDownloads = true;
        blobService.UnrecoverableChunkIndices.Add(1);

        var targetPath = Path.Combine(_restoreDirectory, "recover_progress.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };
        var capturing = new CapturingFileByteProgress();

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(
            filesWithPaths, overwriteExisting: true, fileByteProgress: capturing);

        // Assert — the file went through recovery and reported byte progress under the
        // Recovering status all the way to 100% (so the Progress tab row advances and clears).
        // The fileByteProgress channel is an IProgress<T> that delivers reports asynchronously,
        // so the terminal 100% report can arrive shortly AFTER RestoreFilesWithRemappingAsync
        // returns. Wait for the exact condition the assertion checks (rather than merely "any
        // Recovering report") so this test is deterministic instead of racing the last report.
        Assert.Single(result.CorruptedRecoveryFiles);
        await WaitForAsync(() => capturing.Snapshot().Any(
            r => r.status == FileOperationStatus.Recovering &&
                 r.bytesCompleted == r.fileSize &&
                 r.fileSize == backedUp.FileSize));
        var recovering = capturing.Snapshot().Where(r => r.status == FileOperationStatus.Recovering).ToList();
        Assert.NotEmpty(recovering);
        Assert.Contains(recovering, r => r.bytesCompleted == r.fileSize && r.fileSize == backedUp.FileSize);
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenOneChunkMd5Retries_SiblingsAreNotCancelled()
    {
        // Arrange — a multi-chunk file where ONLY chunk index 1 reports an MD5 mismatch twice
        // and then succeeds. The retry is scoped to that single chunk, so sibling chunks must
        // each download exactly once and the file must restore normally (no recovery).
        SingleChunkMd5RetryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "siblings.bin", 3 * 1024 * 1024);
        Assert.True(backedUp.Chunks.Count >= 3, $"Need >=3 chunks for a middle target, got {backedUp.Chunks.Count}");

        var target = backedUp.Chunks[1].BlobName;
        blobService.TargetBlobName = target;
        blobService.ThrowsForTarget = 2;
        blobService.FailuresEnabled = true;

        var targetPath = Path.Combine(_restoreDirectory, "siblings.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — restored normally (no recovery); the target chunk retried, siblings untouched.
        Assert.Single(result.SuccessfulFiles);
        Assert.Empty(result.CorruptedRecoveryFiles);
        Assert.Equal(0, blobService.BestEffortCalls);
        Assert.Equal(3, blobService.AttemptsFor(target)); // 2 failed + 1 success
        Assert.All(
            backedUp.Chunks.Where(c => c.BlobName != target),
            c => Assert.Equal(1, blobService.AttemptsFor(c.BlobName)));
        Assert.True(File.Exists(targetPath));
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_CountsMd5DownloadRetries()
    {
        // Arrange — every chunk reports an MD5 mismatch twice then succeeds, so the service
        // should record exactly two re-downloads per chunk in TotalMd5DownloadRetries.
        Md5RetryBlobService blobService = new(_encryptionService, throwsPerBlob: 2);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "md5_metric.bin", 100 * 1024);
        blobService.FailuresEnabled = true;

        var targetPath = Path.Combine(_restoreDirectory, "md5_metric.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(filesWithPaths);

        // Assert — restored via retries; counter == 2 retries per chunk.
        Assert.Single(result.SuccessfulFiles);
        Assert.Equal(2L * backedUp.Chunks.Count, restoreService.TotalMd5DownloadRetries);
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenSmallFileRecovers_ReportsRecoveringStatus()
    {
        // Arrange — a file that is "small" by the Progress-tab grouping rule (well under the
        // 16 MB small-file threshold) but still goes through corrupted recovery. The per-file
        // channel must emit Recovering-status byte reports for it; that signal is exactly what
        // the Progress tab keys on to promote a recovering small file out of the aggregate
        // small-file group into its own visible row. Uses the same proven 3 MB multi-chunk
        // recovery setup as the other recovery tests (3 MB < 16 MB, so it is grouped as small).
        PartialRecoveryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "small_recover.bin", 3 * 1024 * 1024);
        Assert.True(backedUp.FileSize < RestoreService.SmallFileThresholdBytes, "Test file must be small.");
        Assert.True(backedUp.Chunks.Count >= 3, $"Need >=3 chunks, got {backedUp.Chunks.Count}");
        // Corrupt normal downloads and leave chunk 1 unrecoverable, so recovery keeps the file in
        // __corrupted__ (a clean, fully-recovered file is promoted to Success instead) and the
        // recovery path — and its Recovering byte reports — is exercised.
        blobService.CorruptNormalDownloads = true;
        blobService.UnrecoverableChunkIndices.Add(1);

        var targetPath = Path.Combine(_restoreDirectory, "small_recover.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };
        var capturing = new CapturingFileByteProgress();

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(
            filesWithPaths, overwriteExisting: true, fileByteProgress: capturing);

        // Assert — recovered, and at least one Recovering report was observed for the small file.
        Assert.Single(result.CorruptedRecoveryFiles);
        await WaitForAsync(() => capturing.Snapshot().Any(r => r.status == FileOperationStatus.Recovering));
        Assert.Contains(capturing.Snapshot(), r => r.status == FileOperationStatus.Recovering);
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenRecoveredWithLoss_EmitsRecoveredTerminalStatus()
    {
        // Arrange — multi-chunk file where chunk 1 is unrecoverable (zero-filled), so the file is
        // recovered WITH data loss. The wrapper must emit a terminal Recovered status (so the
        // Progress tab keeps the row visible rather than showing a clean Complete).
        PartialRecoveryBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "recovered_status.bin", 3 * 1024 * 1024);
        Assert.True(backedUp.Chunks.Count >= 3, $"Need >=3 chunks, got {backedUp.Chunks.Count}");
        blobService.CorruptNormalDownloads = true;
        blobService.UnrecoverableChunkIndices.Add(1);

        var targetPath = Path.Combine(_restoreDirectory, "recovered_status.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };
        var capturing = new CapturingFileByteProgress();

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(
            filesWithPaths, overwriteExisting: true, fileByteProgress: capturing);

        // Assert
        Assert.Single(result.CorruptedRecoveryFiles);
        await WaitForAsync(() => capturing.Snapshot().Any(r => r.status == FileOperationStatus.Recovered));
        Assert.Contains(capturing.Snapshot(), r => r.status == FileOperationStatus.Recovered);
        Assert.DoesNotContain(capturing.Snapshot(), r => r.status == FileOperationStatus.Failed);
    }

    [Fact]
    public async Task RestoreFilesWithRemappingAsync_WhenRecoveryFails_EmitsFailedTerminalStatus()
    {
        // Arrange — normal download corrupted AND best-effort returns null for every chunk, so
        // recovery bails out and the file fails. The wrapper must emit a terminal Failed status
        // (so the Progress tab shows a failed row instead of silently clearing it).
        AllChunksUnrecoverableBlobService blobService = new(_encryptionService);
        await blobService.ConnectAsync("fake", "container");
        RestoreService restoreService = new(_databaseService, blobService, _encryptionService);

        var backedUp = await CreateAndBackupFile(blobService, "failed_status.bin", 3 * 1024 * 1024);
        Assert.True(backedUp.Chunks.Count >= 3, $"Need >=3 chunks, got {backedUp.Chunks.Count}");
        blobService.CorruptNormalDownloads = true;
        blobService.FailBestEffort = true;

        var targetPath = Path.Combine(_restoreDirectory, "failed_status.bin");
        var filesWithPaths = new List<(BackedUpFile file, string targetPath)> { (backedUp, targetPath) };
        var capturing = new CapturingFileByteProgress();

        // Act
        var result = await restoreService.RestoreFilesWithRemappingAsync(
            filesWithPaths, overwriteExisting: true, fileByteProgress: capturing);

        // Assert
        Assert.Single(result.FailedFiles);
        await WaitForAsync(() => capturing.Snapshot().Any(r => r.status == FileOperationStatus.Failed));
        Assert.Contains(capturing.Snapshot(), r => r.status == FileOperationStatus.Failed);
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
        var (chunks, _) = await _chunkingService.ChunkFileForTestAsync(fullPath);
        var fileHash = await ChunkingTestHelper.ComputeFileHashForTestAsync(fullPath);

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

    /// <summary>
    /// Captures the per-file byte-progress reports (bytes, size, index, status) emitted by
    /// the restore pipeline so a test can assert recovery reported progress under the
    /// Recovering status all the way to completion.
    /// </summary>
    private sealed class CapturingFileByteProgress
        : IProgress<(long bytesCompleted, long fileSize, int fileIndex, FileOperationStatus status)>
    {
        private readonly ConcurrentQueue<(long bytesCompleted, long fileSize, int fileIndex, FileOperationStatus status)> _reports = new();

        public void Report((long bytesCompleted, long fileSize, int fileIndex, FileOperationStatus status) value)
            => _reports.Enqueue(value);

        public IReadOnlyList<(long bytesCompleted, long fileSize, int fileIndex, FileOperationStatus status)> Snapshot()
            => _reports.ToArray();
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or the timeout elapses. Used
    /// because the restore pipeline marshals byte-progress reports through an inner
    /// Progress&lt;T&gt; (thread pool), so a report can arrive shortly after the awaited
    /// operation returns.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
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

    private int _repairCount;
    private int _repairedCount;

    /// <summary>Number of times <see cref="RepairChunkAsync"/> was invoked.</summary>
    public int RepairCount => _repairCount;

    /// <summary>Number of chunks that reported <see cref="ChunkRepairOutcome.Repaired"/>.</summary>
    public int RepairedCount => _repairedCount;

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

    public override async Task<ChunkRepairOutcome> RepairChunkAsync(
        ReadOnlyMemory<byte> chunkData, string chunkHash, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _repairCount);
        var outcome = await base.RepairChunkAsync(chunkData, chunkHash, cancellationToken);
        if (outcome == ChunkRepairOutcome.Repaired)
            Interlocked.Increment(ref _repairedCount);
        return outcome;
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

    private int _repairCount;

    /// <summary>Number of times <see cref="RepairChunkAsync"/> was invoked.</summary>
    public int RepairCount => _repairCount;

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

    public override Task<ChunkRepairOutcome> RepairChunkAsync(
        ReadOnlyMemory<byte> chunkData, string chunkHash, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _repairCount);
        return base.RepairChunkAsync(chunkData, chunkHash, cancellationToken);
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

/// <summary>
/// Blob service that throws a <see cref="DownloadIntegrityException"/> (simulated download-time
/// Content-MD5 mismatch) for the first N download attempts of each chunk, then returns good
/// data. Used to verify the restore pipeline re-downloads a chunk on an MD5 mismatch up to
/// MaxIntegrityRetries times before falling back to recovery. The per-blob attempt counter
/// makes the double deterministic regardless of how many chunks the file has.
/// </summary>
internal sealed class Md5RetryBlobService : InMemoryBlobService
{
    private readonly int _throwsPerBlob;
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private int _bestEffortCalls;

    public Md5RetryBlobService(EncryptionService encryptionService, int throwsPerBlob)
        : base(encryptionService) => _throwsPerBlob = throwsPerBlob;

    /// <summary>When false the double behaves normally; set true after backup to start failing downloads.</summary>
    public bool FailuresEnabled { get; set; }

    /// <summary>Number of best-effort (recovery) download calls observed.</summary>
    public int BestEffortCalls => Volatile.Read(ref _bestEffortCalls);

    /// <summary>Total normal-download attempts recorded for a given chunk blob.</summary>
    public int AttemptsFor(string blobName) => _attempts.TryGetValue(blobName, out var n) ? n : 0;

    public override async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        var attempt = _attempts.AddOrUpdate(blobName, 1, (_, n) => n + 1);
        if (FailuresEnabled && attempt <= _throwsPerBlob)
            throw new DownloadIntegrityException($"Simulated Content-MD5 mismatch for {blobName}", blobName);
        return await base.DownloadChunkAsync(blobName, cancellationToken);
    }

    public override Task<byte[]?> DownloadChunkBestEffortAsync(string blobName, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _bestEffortCalls);
        return base.DownloadChunkBestEffortAsync(blobName, cancellationToken);
    }
}

/// <summary>
/// Blob service that throws a <see cref="DownloadIntegrityException"/> (simulated Content-MD5
/// mismatch) for the first <see cref="ThrowsForTarget"/> download attempts of ONE specific chunk
/// blob (<see cref="TargetBlobName"/>) and serves every other chunk normally. Used to verify that
/// a single chunk's MD5 retries do not cancel sibling downloads.
/// </summary>
internal sealed class SingleChunkMd5RetryBlobService : InMemoryBlobService
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private int _bestEffortCalls;

    public SingleChunkMd5RetryBlobService(EncryptionService encryptionService)
        : base(encryptionService) { }

    /// <summary>When false the double behaves normally; set true after backup to start failing the target.</summary>
    public bool FailuresEnabled { get; set; }

    /// <summary>The single chunk blob name that should fail; null disables targeting.</summary>
    public string? TargetBlobName { get; set; }

    /// <summary>How many of the target's first download attempts throw before it succeeds.</summary>
    public int ThrowsForTarget { get; set; }

    public int BestEffortCalls => Volatile.Read(ref _bestEffortCalls);

    public int AttemptsFor(string blobName) => _attempts.TryGetValue(blobName, out var n) ? n : 0;

    public override async Task<byte[]> DownloadChunkAsync(string blobName, CancellationToken cancellationToken = default)
    {
        var attempt = _attempts.AddOrUpdate(blobName, 1, (_, n) => n + 1);
        if (FailuresEnabled && blobName == TargetBlobName && attempt <= ThrowsForTarget)
            throw new DownloadIntegrityException($"Simulated Content-MD5 mismatch for {blobName}", blobName);
        return await base.DownloadChunkAsync(blobName, cancellationToken);
    }

    public override Task<byte[]?> DownloadChunkBestEffortAsync(string blobName, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _bestEffortCalls);
        return base.DownloadChunkBestEffortAsync(blobName, cancellationToken);
    }
}
