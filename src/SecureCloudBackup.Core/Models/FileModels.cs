using SecureCloudBackup.Core.Services;

namespace SecureCloudBackup.Core.Models;

/// <summary>
/// Represents metadata about a backed up file.
/// </summary>
public class BackedUpFile
{
    public int Id { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public string BlobName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    
    /// <summary>
    /// SHA-256 hash of the complete file for integrity verification.
    /// </summary>
    public string FileHash { get; set; } = string.Empty;
    
    public List<ChunkInfo> Chunks { get; set; } = [];
    public DateTime BackedUpAt { get; set; }
    public BackupStatus Status { get; set; } = BackupStatus.Pending;
    
    /// <summary>
    /// Backup metadata format version.
    /// </summary>
    public int MetadataVersion { get; set; } = 1;
    
    /// <summary>
    /// The Azure storage tier of the metadata blob.
    /// This is populated when fetching from Azure and indicates the current tier.
    /// </summary>
    public StorageTier? CurrentStorageTier { get; set; }
}

/// <summary>
/// Represents information about a file chunk for delta sync.
/// </summary>
public class ChunkInfo
{
    public int Index { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
    
    /// <summary>
    /// SHA-256 hash of chunk content for content-addressable storage.
    /// </summary>
    public string Hash { get; set; } = string.Empty;
    
    public string BlobName { get; set; } = string.Empty;
}

/// <summary>
/// Carries chunk metadata and raw data through the CDC-to-upload pipeline.
/// <para>
/// <c>Data</c> may be a rented ArrayPool buffer (oversized) or, for chunks
/// large enough to skip the pool under B33, an exactly-sized <c>byte[]</c>
/// allocated by the producer. Use <see cref="Length"/> for the actual data
/// extent.
/// </para>
/// <para>
/// <c>ChargedBytes</c> (B30) is the amount the producer charged to the
/// shared <see cref="MemoryBudget"/> when allocating <c>Data</c>. The
/// consumer MUST release exactly this amount when it is done with the
/// payload, regardless of <see cref="Length"/>. This decouples accounting
/// from the user-visible chunk size, so a chunk whose ArrayPool tier
/// rounded up from 80 MB to 128 MB charges (and releases) the actual
/// 128 MB residency.
/// </para>
/// <para>
/// <c>ReturnToPool</c> (B33) tells the consumer whether to return
/// <c>Data</c> to <see cref="System.Buffers.ArrayPool{T}.Shared"/> after
/// upload. <c>true</c> matches the pre-B33 behaviour. <c>false</c> means
/// <c>Data</c> was either an exact-sized GC allocation that must be
/// dropped on the floor (legacy B33 path with no pool) OR is owned by
/// <c>BufferPool</c> and must be returned there instead -- the
/// consumer's dispatch is "if BufferPool != null, return there;
/// else if ReturnToPool, return to ArrayPool.Shared; else drop".
/// Returning a non-pool array to the shared pool silently corrupts
/// the pool's invariants, so the flag is load-bearing.
/// </para>
/// <para>
/// <c>BufferPool</c> (B70, the unification of B37's
/// <c>LargeChunkBufferPool</c> and B69's <c>BudgetedMemoryPool</c>) is
/// the per-operation <see cref="ChunkBufferPool"/> that owns
/// <c>Data</c>. The same field carries either the small-chunk-path
/// pool (configured with
/// <see cref="ChunkBufferPool.SmallChunkBucketSizes"/>) or the
/// large-chunk-path pool (configured with
/// <see cref="ChunkBufferPool.LargeChunkBucketSizes"/>); the consumer
/// does not care which geometry it is because the return contract is
/// identical. <c>null</c> for chunks produced by callers that did not
/// supply a pool (CDC benchmarks) and for chunks that fell back to
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/> (in which case
/// <c>ReturnToPool</c> is <c>true</c>).
/// </para>
/// </summary>
public record ChunkPayload(
    ChunkInfo Info,
    byte[] Data,
    int Length,
    long ChargedBytes,
    bool ReturnToPool,
    ChunkBufferPool? BufferPool = null);

public enum BackupStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Excluded
}

/// <summary>
/// Represents a file change detected by the file system watcher.
/// </summary>
public class FileChangeEvent
{
    public string FilePath { get; set; } = string.Empty;
    public FileChangeType ChangeType { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

public enum FileChangeType
{
    Created,
    Modified,
    Deleted,
    Renamed
}
