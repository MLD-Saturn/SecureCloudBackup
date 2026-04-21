using System.Diagnostics;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Service for managing the chunk index, tracking chunk-to-file associations,
/// detecting orphans, and maintaining storage health.
/// </summary>
public partial class ChunkIndexService
{
    private readonly LocalDatabaseService _databaseService;
    private readonly IBlobStorageService _blobService;
    private readonly EncryptionService _encryptionService;
    
    private const string IndexBackupBlobName = "index/chunk-index-backup.enc";

    /// <summary>
    /// Event raised for diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;

    [Conditional("DIAGNOSTICLOG")]
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [ChunkIndex] {message}");
    }

    public ChunkIndexService(LocalDatabaseService databaseService, IBlobStorageService blobService, EncryptionService encryptionService)
    {
        ArgumentNullException.ThrowIfNull(databaseService);
        ArgumentNullException.ThrowIfNull(blobService);
        ArgumentNullException.ThrowIfNull(encryptionService);
        _databaseService = databaseService;
        _blobService = blobService;
        _encryptionService = encryptionService;
    }

    #region Reference Management

    /// <summary>
    /// Adds a reference from a file to a chunk.
    /// Creates the chunk entry if it doesn't exist.
    /// </summary>
    /// <param name="chunkHash">The chunk hash</param>
    /// <param name="filePath">The file path referencing this chunk</param>
    /// <param name="chunkIndex">The index of this chunk within the file</param>
    /// <param name="sizeBytes">Size of the chunk in bytes</param>
    /// <param name="tier">Storage tier of the chunk</param>
    /// <param name="isNewChunk">Whether this chunk was just uploaded (vs. deduplicated)</param>
    public void AddReference(string chunkHash, string filePath, int chunkIndex, 
        long sizeBytes, StorageTier tier, bool isNewChunk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var entry = _databaseService.GetChunkIndexEntry(chunkHash);

        if (entry == null)
        {
            // New chunk - create entry. ReferenceCount starts at 0;
            // the UpsertChunkFileRef + GetReferenceCountForChunk pair
            // below sets the authoritative count.
            entry = new ChunkIndexEntry
            {
                ChunkHash = chunkHash,
                FirstUploadedAt = DateTime.UtcNow,
                OriginalUploaderPath = filePath,
                CurrentTier = tier,
                SizeBytes = sizeBytes,
                LastVerifiedAt = DateTime.UtcNow,
                ReferenceCount = 0,
                ReferencingFiles = []
            };
            Log($"Created new chunk index entry for {chunkHash[..8]}...");
        }

        var referencedAt = DateTime.UtcNow;

        // Write to the canonical reverse-index FIRST. UpsertChunkFileRef
        // is idempotent on the (file_path, chunk_hash, chunk_index)
        // triple - if the same triple is upserted twice the row's
        // referenced_at is updated and no duplicate is created.
        // This is the single source of truth for "which files reference
        // this chunk". Pre-C-5 this code maintained a parallel in-memory
        // list (entry.ReferencingFiles) that worked under LiteDB but not
        // under SQLite (where GetChunkIndexEntry leaves the list empty
        // by design); SQLite-by-default exposed the divergence and we
        // now derive ReferenceCount from the canonical source instead.
        _databaseService.UpsertChunkFileRef(filePath, chunkHash, chunkIndex, referencedAt);

        // Read back the authoritative count + referencing files so the
        // entry we save AND the in-memory ChunkIndexEntry surface match
        // the on-disk reverse-index state. Tests and downstream callers
        // that inspect entry.ReferencingFiles see consistent data
        // regardless of which backend is in use.
        var referencingFiles = _databaseService.GetReferencingFilesForChunk(chunkHash);
        entry.ReferencingFiles = referencingFiles;
        entry.ReferenceCount = referencingFiles.Count;
        Log($"Reference for {filePath} -> chunk {chunkHash[..8]}... (ref count: {entry.ReferenceCount})");

        // Update tier if this was a new upload
        if (isNewChunk)
        {
            entry.CurrentTier = tier;
            entry.LastVerifiedAt = DateTime.UtcNow;
        }

        _databaseService.SaveChunkIndexEntry(entry);
    }

    /// <summary>
    /// Removes all references from a file to its chunks.
    /// Deletes chunks that reach reference count 0.
    /// </summary>
    /// <param name="filePath">The file path to remove references for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of orphaned chunks deleted</returns>
    public async Task<int> RemoveFileReferencesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Log($"Removing all chunk references for file: {filePath}");

        var entries = _databaseService.GetChunkEntriesForFile(filePath);
        var deletedCount = 0;

        // Mutate the canonical reverse index FIRST. Drop every row
        // that pointed at this file path; the per-chunk loop below
        // then reads the post-delete count back via
        // GetReferenceCountForChunk to decide orphan-or-keep. Pre-C-5
        // this code mutated entry.ReferencingFiles in memory which
        // was empty under SQLite, causing every chunk reachable from
        // this file to be incorrectly classified as an orphan.
        _databaseService.DeleteChunkFileRefsForFile(filePath);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var newCount = _databaseService.GetReferenceCountForChunk(entry.ChunkHash);
            entry.ReferenceCount = newCount;
            entry.ReferencingFiles = _databaseService.GetReferencingFilesForChunk(entry.ChunkHash);

            if (newCount == 0)
            {
                // Delete the orphaned chunk immediately
                Log($"Chunk {entry.ChunkHash[..8]}... has no references, deleting from Azure...");
                try
                {
                    await _blobService.DeleteBlobAsync($"chunks/{entry.ChunkHash}", cancellationToken);
                    _databaseService.DeleteChunkIndexEntry(entry.ChunkHash);
                    // Defensive: ensure no stragglers in the reverse index
                    // for this chunk (e.g. if the graph was inconsistent
                    // before this call).
                    _databaseService.DeleteChunkFileRefsForChunk(entry.ChunkHash);
                    deletedCount++;
                    Log($"Deleted orphaned chunk {entry.ChunkHash[..8]}...");
                }
                catch (Exception ex)
                {
                    Log($"Failed to delete chunk {entry.ChunkHash[..8]}...: {ex.Message}");
                    // Keep the entry marked as orphan for later cleanup
                    _databaseService.SaveChunkIndexEntry(entry);
                }
            }
            else
            {
                _databaseService.SaveChunkIndexEntry(entry);
                Log($"Updated chunk {entry.ChunkHash[..8]}... ref count to {entry.ReferenceCount}");
            }
        }

        return deletedCount;
    }

    /// <summary>
    /// Updates chunk references when a file is modified.
    /// Removes references to old chunks and adds references to new chunks.
    /// </summary>
    /// <param name="filePath">The file path</param>
    /// <param name="oldChunkHashes">Hashes of chunks from previous version</param>
    /// <param name="newChunks">New chunk information</param>
    /// <param name="tier">Storage tier for new chunks</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task UpdateFileChunksAsync(
        string filePath,
        IList<string> oldChunkHashes,
        IList<(string hash, int index, long size, bool isNew)> newChunks,
        StorageTier tier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        Log($"Updating chunk references for modified file: {filePath}");

        // Find chunks that are no longer used by this file
        var newHashSet = new HashSet<string>(newChunks.Select(c => c.hash), StringComparer.Ordinal);
        var removedHashes = oldChunkHashes.Where(h => !newHashSet.Contains(h)).ToList();

        // Remove references to old chunks
        foreach (var hash in removedHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Drop the reverse-index rows binding this file to this
            // chunk FIRST, then read the canonical count back to
            // decide orphan-or-keep. Pre-C-5 this code mutated
            // entry.ReferencingFiles in memory which was empty under
            // SQLite (see AddReference for the wider context).
            _databaseService.DeleteChunkFileRefsForFileAndChunk(filePath, hash);

            var entry = _databaseService.GetChunkIndexEntry(hash);
            if (entry == null) continue;

            var newCount = _databaseService.GetReferenceCountForChunk(hash);
            entry.ReferenceCount = newCount;
            entry.ReferencingFiles = _databaseService.GetReferencingFilesForChunk(hash);

            if (newCount == 0)
            {
                // Delete immediately
                try
                {
                    await _blobService.DeleteBlobAsync($"chunks/{hash}", cancellationToken);
                    _databaseService.DeleteChunkIndexEntry(hash);
                    _databaseService.DeleteChunkFileRefsForChunk(hash);
                    Log($"Deleted orphaned chunk {hash[..8]}... (removed from modified file)");
                }
                catch (Exception ex)
                {
                    Log($"Failed to delete chunk {hash[..8]}...: {ex.Message}");
                    _databaseService.SaveChunkIndexEntry(entry);
                }
            }
            else
            {
                _databaseService.SaveChunkIndexEntry(entry);
            }
        }

        // Add references to new chunks
        foreach (var (hash, index, size, isNew) in newChunks)
        {
            AddReference(hash, filePath, index, size, tier, isNew);
        }
    }

    #endregion

    #region Orphan Detection and Cleanup

    /// <summary>
    /// Scans for orphaned chunks in Azure that aren't referenced by any file.
    /// Uses a lightweight index summary (hash + refcount only) to minimize memory,
    /// then parallel Azure property queries for orphan details.
    /// </summary>
    /// <param name="progress">Progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Scan result with orphan details</returns>
    public async Task<OrphanScanResult> ScanForOrphansAsync(
        IProgress<(int scanned, int total, string currentChunk)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Log("Starting orphan scan...");
        var startTime = DateTime.UtcNow;
        var result = new OrphanScanResult { ScannedAt = startTime };

        // Get all chunk blobs from Azure
        var azureChunks = await ListAzureChunksAsync(cancellationToken);
        var totalChunks = azureChunks.Count;
        Log($"Found {totalChunks} chunks in Azure");

        // Bulk-load lightweight chunk index summary for fast lookups
        // Only loads hash + refcount + size + tier — not the full ReferencingFiles list
        // At 1M chunks this uses ~80 MB vs ~1.5 GB for full ChunkIndexEntry objects
        var indexSummary = _databaseService.GetChunkIndexSummaryMap();
        Log($"Loaded lightweight index summary for {indexSummary.Count} chunks");

        // Phase 1: Identify orphans using local lookups only (no HTTP)
        var orphanHashes = new List<(string hash, long cachedSize, StorageTier cachedTier)>();
        var scanned = 0;
        foreach (var chunkHash in azureChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;

            if (scanned % 500 == 0 || scanned == totalChunks)
            {
                progress?.Report((scanned, totalChunks, chunkHash));
            }

            if (indexSummary.TryGetValue(chunkHash, out var summary))
            {
                if (summary.ReferenceCount == 0)
                {
                    orphanHashes.Add((chunkHash, summary.SizeBytes, summary.Tier));
                }
            }
            else
            {
                // Not in index at all — orphan with no cached info
                orphanHashes.Add((chunkHash, 0, StorageTier.Hot));
            }
        }

        Log($"Identified {orphanHashes.Count} potential orphans. Fetching details in parallel...");

        // Phase 2: Fetch size/tier for orphans in parallel from Azure
        const int maxParallelQueries = 32;
        int queried = 0;
        object resultLock = new();

        await Parallel.ForEachAsync(
            orphanHashes,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelQueries,
                CancellationToken = cancellationToken
            },
            async (item, ct) =>
            {
                var (chunkHash, cachedSize, cachedTier) = item;
                long sizeBytes = cachedSize;
                StorageTier tier = cachedTier;

                try
                {
                    var (size, blobTier) = await GetChunkInfoFromAzureAsync(chunkHash, ct);
                    sizeBytes = size;
                    tier = blobTier;
                }
                catch
                {
                    // Use cached values from index summary
                }

                var orphanEntry = new ChunkIndexEntry
                {
                    ChunkHash = chunkHash,
                    SizeBytes = sizeBytes,
                    CurrentTier = tier,
                    ReferenceCount = 0,
                    ReferencingFiles = []
                };

                lock (resultLock)
                {
                    result.OrphanedChunks.Add(orphanEntry);
                    result.TotalOrphanSizeBytes += sizeBytes;
                }

                var count = Interlocked.Increment(ref queried);
                if (count % 50 == 0 || count == orphanHashes.Count)
                {
                    progress?.Report((scanned, totalChunks, $"Querying orphan details... {count}/{orphanHashes.Count}"));
                }
            });

        result.ChunksScanned = scanned;
        result.ScanDuration = DateTime.UtcNow - startTime;

        Log($"Orphan scan complete: {result.OrphanedChunks.Count} orphans found, " +
            $"{FormatHelper.FormatBytes(result.TotalOrphanSizeBytes)} total");

        return result;
    }

    /// <summary>
    /// Deletes orphaned chunks from Azure using parallel blob deletions.
    /// </summary>
    /// <param name="orphans">List of orphaned chunks to delete</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cleanup result</returns>
    public async Task<CleanupResult> CleanupOrphansAsync(
        IList<ChunkIndexEntry> orphans,
        IProgress<(int deleted, int total, string currentChunk)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Log($"Starting parallel cleanup of {orphans.Count} orphaned chunks...");
        var result = new CleanupResult { CleanedAt = DateTime.UtcNow };

        const int maxParallelDeletes = 128;
        int deleted = 0;
        object resultLock = new();

        await Parallel.ForEachAsync(
            orphans,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelDeletes,
                CancellationToken = cancellationToken
            },
            async (orphan, ct) =>
            {
                try
                {
                    await _blobService.DeleteBlobAsync($"chunks/{orphan.ChunkHash}", ct);
                    _databaseService.DeleteChunkIndexEntry(orphan.ChunkHash);

                    lock (resultLock)
                    {
                        result.ChunksDeleted++;
                        result.BytesFreed += orphan.SizeBytes;
                    }

                    var count = Interlocked.Increment(ref deleted);
                    if (count % 20 == 0 || count == orphans.Count)
                    {
                        progress?.Report((count, orphans.Count, orphan.ChunkHash));
                        Log($"Cleanup progress: {count}/{orphans.Count} orphans deleted");
                    }
                }
                catch (Exception ex)
                {
                    lock (resultLock)
                    {
                        result.FailedDeletions++;
                        result.Errors.Add($"Failed to delete {orphan.ChunkHash[..8]}...: {ex.Message}");
                    }
                    Log($"Failed to delete orphan {orphan.ChunkHash[..8]}...: {ex.Message}");
                }
            });

        Log($"Cleanup complete: {result.ChunksDeleted} deleted, {result.FailedDeletions} failed, " +
            $"{FormatHelper.FormatBytes(result.BytesFreed)} freed");

        return result;
    }

    /// <summary>
    /// Performs a lightweight verification after backup to ensure chunk references are consistent.
    /// </summary>
    /// <param name="filePath">The file that was just backed up</param>
    /// <param name="chunkHashes">The chunk hashes for this file</param>
    public void VerifyBackupConsistency(string filePath, IList<string> chunkHashes)
    {
        Log($"Verifying backup consistency for {filePath}...");

        foreach (var hash in chunkHashes)
        {
            var entry = _databaseService.GetChunkIndexEntry(hash);
            if (entry == null)
            {
                Log($"WARNING: Chunk {hash[..8]}... not found in index after backup!");
                continue;
            }

            // Canonical reverse-index lookup; do not trust
            // entry.ReferencingFiles which the SQLite backend leaves
            // empty by design.
            var referencingFiles = _databaseService.GetReferencingFilesForChunk(hash);
            if (!referencingFiles.Any(r =>
                r.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
            {
                Log($"WARNING: File {filePath} not found in references for chunk {hash[..8]}...");
            }
        }
    }

    #endregion

    #region Index Statistics

    /// <summary>
    /// Gets summary statistics for the chunk index.
    /// </summary>
    /// <remarks>
    /// Single-pass aggregation over the lightweight summary map (hash +
    /// refcount + size + tier per chunk; no <c>ReferencingFiles</c> list
    /// materialised). Pre-fix this method called <see
    /// cref="LocalDatabaseService.GetAllChunkIndexEntries"/> which
    /// allocated full <c>ChunkIndexEntry</c> objects (~50–100 MB at 500K
    /// chunks) and walked the result list 9× via LINQ. The summary-map
    /// path is the same one already used by <see cref="ScanForOrphansAsync"/>
    /// and is roughly an order of magnitude lighter.
    /// </remarks>
    public ChunkIndexSummary GetIndexSummary()
    {
        var summary = new ChunkIndexSummary
        {
            TierBreakdown = []
        };

        // Pre-seed every tier so callers get a stable shape even when a
        // tier has zero chunks.
        foreach (StorageTier tier in Enum.GetValues<StorageTier>())
        {
            summary.TierBreakdown[tier] = new TierStatistics();
        }

        var summaryMap = _databaseService.GetChunkIndexSummaryMap();
        foreach (var (_, info) in summaryMap)
        {
            summary.TotalChunks++;
            summary.TotalSizeBytes += info.SizeBytes;

            if (info.ReferenceCount == 0)
            {
                summary.OrphanCount++;
                summary.OrphanSizeBytes += info.SizeBytes;
            }
            else if (info.ReferenceCount > 1)
            {
                summary.SharedChunks++;
                // Dedup savings = bytes that would have been uploaded if
                // every reference required its own copy: size × (refs - 1).
                summary.DeduplicationSavingsBytes += info.SizeBytes * (info.ReferenceCount - 1);
            }

            var tierStats = summary.TierBreakdown[info.Tier];
            tierStats.ChunkCount++;
            tierStats.TotalSizeBytes += info.SizeBytes;
        }

        summary.LastFullRebuildAt = _databaseService.GetIndexMetadata("LastFullRebuildAt");
        summary.LastAzureSyncAt = _databaseService.GetIndexMetadata("LastAzureSyncAt");

        return summary;
    }

    #endregion
}
