namespace AzureBackup.Core.Models;

/// <summary>
/// Unified progress event types for backup, restore, and mirror operations.
/// All three operations report progress through the same model so the UI
/// can use a single progress tab implementation.
/// </summary>
public enum OperationProgressType
{
    /// <summary>A file has started processing (downloading/uploading).</summary>
    FileStarted,

    /// <summary>Byte-level progress update for an active file.</summary>
    FileProgress,

    /// <summary>A file has completed successfully and been written to disk.</summary>
    FileCompleted,

    /// <summary>A file has failed (will remain visible in the progress list).</summary>
    FileFailed,

    /// <summary>
    /// A file finished via corrupted recovery with at least one zero-filled chunk
    /// (partial data loss). The row stays visible with a <see cref="FileOperationStatus.Recovered"/>
    /// status so the user knows the restored file is incomplete.
    /// </summary>
    FileRecovered,

    /// <summary>Aggregate progress for the small-file group (≤100 MB files).</summary>
    SmallFileGroupProgress,

    /// <summary>The operation phase has changed (e.g., "Phase 1/2: Small files").</summary>
    PhaseChanged,

    /// <summary>The operation has fully completed.</summary>
    OperationCompleted
}

/// <summary>
/// Status of an individual file within a batch operation.
/// Displayed in the per-file status column on the progress tab.
/// </summary>
public enum FileOperationStatus
{
    Queued,
    Downloading,
    Uploading,
    Writing,
    Verifying,
    Complete,
    Failed,
    Retrying,

    /// <summary>
    /// The file failed its normal restore (e.g. a chunk integrity failure) and is
    /// being rebuilt by the best-effort corrupted-recovery path. Shown on the
    /// per-file progress row so the user can see the file is actively recovering
    /// rather than frozen.
    /// </summary>
    Recovering,

    /// <summary>
    /// Terminal state: the file was rebuilt by corrupted recovery but at least one chunk
    /// could not be recovered and was zero-filled (partial data loss). The row stays visible
    /// with distinct styling so the user knows the restored file is incomplete.
    /// </summary>
    Recovered
}

/// <summary>
/// Unified progress report for backup, restore, and mirror operations.
/// Each event carries the data relevant to its <see cref="Type"/>; unused fields are default.
/// </summary>
public sealed record OperationProgressReport
{
    /// <summary>The kind of progress event.</summary>
    public required OperationProgressType Type { get; init; }

    // ── File-level fields (FileStarted, FileProgress, FileCompleted, FileFailed) ──

    /// <summary>Index of the file in the overall operation (0-based).</summary>
    public int FileIndex { get; init; }

    /// <summary>Display name of the file (filename only, no path).</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Total size of the file in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>Bytes transferred so far for this file.</summary>
    public long FileBytesProcessed { get; init; }

    /// <summary>Current status for display in the per-file status column.</summary>
    public FileOperationStatus FileStatus { get; init; }

    /// <summary>Error message when <see cref="Type"/> is <see cref="OperationProgressType.FileFailed"/>.</summary>
    public string? ErrorMessage { get; init; }

    // ── Small file group fields (SmallFileGroupProgress) ──

    /// <summary>Number of small files completed so far.</summary>
    public int SmallFilesCompleted { get; init; }

    /// <summary>Total number of small files in this operation.</summary>
    public int SmallFilesTotal { get; init; }

    /// <summary>Total bytes transferred across all small files.</summary>
    public long SmallFilesBytesProcessed { get; init; }

    /// <summary>Total bytes across all small files.</summary>
    public long SmallFilesTotalBytes { get; init; }

    // ── Overall fields (all event types carry these for aggregate tracking) ──

    /// <summary>Total bytes transferred across all files in the operation.</summary>
    public long TotalBytesProcessed { get; init; }

    /// <summary>Total bytes to transfer across all files.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Number of files completed (success + failed).</summary>
    public int TotalFilesCompleted { get; init; }

    /// <summary>Total number of files in the operation.</summary>
    public int TotalFiles { get; init; }

    // ── Phase fields (PhaseChanged) ──

    /// <summary>Human-readable phase description (e.g., "Phase 1/2: Small files").</summary>
    public string? PhaseDescription { get; init; }

    // ── Completion fields (OperationCompleted) ──

    /// <summary>Number of files that succeeded.</summary>
    public int SucceededCount { get; init; }

    /// <summary>Number of files that failed.</summary>
    public int FailedCount { get; init; }

    /// <summary>Number of files recovered to __corrupted__ folder.</summary>
    public int CorruptedRecoveryCount { get; init; }

    /// <summary>Total wall-clock duration of the operation.</summary>
    public TimeSpan Elapsed { get; init; }
}
