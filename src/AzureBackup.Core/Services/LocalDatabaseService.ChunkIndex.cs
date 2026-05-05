using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Chunk index, statistics, and secure reset operations.
/// </summary>
public partial class LocalDatabaseService
{
    #region Chunk Index

    /// <summary>
    /// Gets a chunk index entry by hash.
    /// </summary>
    public ChunkIndexEntry? GetChunkIndexEntry(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        return GetBackend().GetChunkIndexEntry(chunkHash);
    }

    /// <summary>
    /// Saves or updates a chunk index entry.
    /// </summary>
    public void SaveChunkIndexEntry(ChunkIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        GetBackend().SaveChunkIndexEntry(entry);
    }

    /// <summary>
    /// Bulk-inserts chunk index entries. Use only after ClearChunkIndex when no existing entries exist.
    /// Significantly faster than individual SaveChunkIndexEntry calls for rebuilds.
    /// </summary>
    public void BulkInsertChunkIndexEntries(IEnumerable<ChunkIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        GetBackend().BulkInsertChunkIndexEntries(entries);
    }

    /// <summary>
    /// Deletes a chunk index entry.
    /// </summary>
    public void DeleteChunkIndexEntry(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        GetBackend().DeleteChunkIndexEntry(chunkHash);
    }

    /// <summary>
    /// Gets all chunk index entries.
    /// </summary>
    public List<ChunkIndexEntry> GetAllChunkIndexEntries() => GetBackend().GetAllChunkIndexEntries();

    /// <summary>
    /// Gets a lightweight summary of all chunk index entries for fast lookups.
    /// Returns only the hash, reference count, size, and tier — without loading
    /// the ReferencingFiles list, which dominates memory at scale.
    /// At 1M chunks, this uses ~80 MB vs ~1.5 GB for full entries.
    /// </summary>
    public Dictionary<string, (int ReferenceCount, long SizeBytes, StorageTier Tier)> GetChunkIndexSummaryMap()
        => GetBackend().GetChunkIndexSummaryMap();

    /// <summary>
    /// Gets chunk entries that reference a specific file.
    /// </summary>
    /// <remarks>
    /// Uses the reverse <c>chunk_file_refs</c> index (Phase 5 / P3): an indexed
    /// <c>FilePath</c> lookup returns the matching chunk hashes, then a single
    /// indexed lookup against <see cref="ChunkIndexEntry.ChunkHash"/> batches
    /// all entry fetches into one round trip.
    /// </remarks>
    public List<ChunkIndexEntry> GetChunkEntriesForFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return GetBackend().GetChunkEntriesForFile(filePath);
    }

    /// <summary>
    /// Adds or updates a single reverse-index row. Idempotent: a row for the same
    /// <c>(FilePath, ChunkHash, ChunkIndex)</c> triple is replaced rather than
    /// duplicated.
    /// </summary>
    internal void UpsertChunkFileRef(string filePath, string chunkHash, int chunkIndex, DateTime referencedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        GetBackend().UpsertChunkFileRef(filePath, chunkHash, chunkIndex, referencedAt);
    }

    /// <summary>
    /// Bulk-inserts reverse-index rows in a single transaction. Used by the
    /// one-time rebuild path and any future path that re-registers many
    /// references in one go.
    /// </summary>
    internal void BulkInsertChunkFileRefs(IEnumerable<ChunkFileRefRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        GetBackend().BulkInsertChunkFileRefs(rows);
    }

    /// <summary>
    /// Returns every reverse-index row. Used by the Azure index backup
    /// path so the encrypted blob carries the (file_path, chunk_hash,
    /// chunk_index) graph alongside the primary chunk_index entries.
    /// Without this, restoring from an Azure index backup would leave
    /// chunk_file_refs empty.
    /// </summary>
    internal List<ChunkFileRefRow> GetAllChunkFileRefs() => GetBackend().GetAllChunkFileRefs();

    /// <summary>
    /// Deletes every reverse-index row for a single file path. Called when a
    /// backed-up file is removed.
    /// </summary>
    internal int DeleteChunkFileRefsForFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return GetBackend().DeleteChunkFileRefsForFile(filePath);
    }

    /// <summary>
    /// Deletes every reverse-index row for a single chunk hash. Called when a
    /// chunk itself is deleted (e.g. orphan cleanup).
    /// </summary>
    internal int DeleteChunkFileRefsForChunk(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        return GetBackend().DeleteChunkFileRefsForChunk(chunkHash);
    }

    /// <summary>
    /// Deletes reverse-index rows binding a specific file path to a specific
    /// chunk hash. Called when a chunk is no longer referenced by a file but is
    /// still referenced by others.
    /// </summary>
    internal int DeleteChunkFileRefsForFileAndChunk(string filePath, string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        return GetBackend().DeleteChunkFileRefsForFileAndChunk(filePath, chunkHash);
    }

    /// <summary>
    /// Returns <c>true</c> when the reverse <c>chunk_file_refs</c> index has
    /// been built for the current database. Checked by the UI at startup so
    /// it can decide whether to show the one-time rebuild progress dialog.
    /// </summary>
    public bool IsReverseChunkIndexBuilt() => GetBackend().IsReverseChunkIndexBuilt();

    /// <summary>
    /// One-time migration that populates the reverse <c>chunk_file_refs</c> index
    /// from the legacy <see cref="ChunkIndexEntry.ReferencingFiles"/> list on the
    /// primary collection. Safe to call repeatedly; a no-op once the
    /// <c>ReverseIndexBuiltAt</c> metadata marker exists.
    /// </summary>
    /// <param name="progress">
    /// Optional reporter receiving <c>(processedChunks, totalChunks)</c>. The UI
    /// layer drives a modal progress dialog from this callback.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation propagates cooperatively between chunk batches.
    /// </param>
    public void RebuildReverseChunkIndex(
        IProgress<(int processed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
        => GetBackend().RebuildReverseChunkIndex(progress, cancellationToken);

    /// <summary>
    /// Runs an explicit WAL checkpoint, flushing the WAL into the main data
    /// file. Keeps the WAL companion file from growing unbounded across long
    /// app sessions; SqliteBackend manages its own checkpoint timer in
    /// addition to honouring this explicit call.
    /// </summary>
    public void Checkpoint() => GetBackend().Checkpoint();

    /// <summary>
    /// Gets orphaned chunks (reference count = 0).
    /// </summary>
    public List<ChunkIndexEntry> GetOrphanedChunks() => GetBackend().GetOrphanedChunks();

    /// <summary>
    /// Returns the authoritative reference count for <paramref name="chunkHash"/>
    /// computed from the canonical <c>chunk_file_refs</c> reverse-index
    /// collection rather than from the cached
    /// <see cref="ChunkIndexEntry.ReferenceCount"/> column.
    /// </summary>
    public int GetReferenceCountForChunk(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        return GetBackend().GetReferenceCountForChunk(chunkHash);
    }

    /// <summary>
    /// Returns every <see cref="ChunkFileReference"/> for
    /// <paramref name="chunkHash"/> from the canonical
    /// <c>chunk_file_refs</c> reverse-index collection. Order is not
    /// specified.
    /// </summary>
    public List<ChunkFileReference> GetReferencingFilesForChunk(string chunkHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        return GetBackend().GetReferencingFilesForChunk(chunkHash);
    }

    /// <summary>
    /// Clears all chunk index entries.
    /// </summary>
    public void ClearChunkIndex() => GetBackend().ClearChunkIndex();

    /// <summary>
    /// Gets index metadata by key.
    /// </summary>
    public DateTime? GetIndexMetadata(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return GetBackend().GetIndexMetadata(key);
    }

    /// <summary>
    /// Sets index metadata by key.
    /// </summary>
    public void SetIndexMetadata(string key, DateTime value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        GetBackend().SetIndexMetadata(key, value);
    }

    /// <summary>
    /// Returns every (key, value) row in the index_metadata collection.
    /// </summary>
    public IReadOnlyDictionary<string, DateTime> GetAllIndexMetadata() => GetBackend().GetAllIndexMetadata();

    /// <summary>
    /// Gets the total count of chunks in the index.
    /// </summary>
    public int GetChunkIndexCount() => GetBackend().GetChunkIndexCount();

    #endregion

    #region Statistics

    /// <summary>
    /// Gets backup statistics.
    /// </summary>
    public BackupStatistics GetStatistics() => GetBackend().GetStatistics();

    #endregion

    #region Reset and Secure Delete

    /// <summary>
    /// Securely deletes all data and resets the database.
    /// Overwrites sensitive data before deletion to prevent recovery.
    /// After calling this method, the database is closed and the application
    /// should restart or call Initialize with a new password.
    /// </summary>
    public void SecureReset()
    {
        if (_sqliteBackend == null) return;
        _sqliteBackend.SecureReset();
        _sqliteBackend = null;
        _databasePath = null;
    }

    #endregion
}
