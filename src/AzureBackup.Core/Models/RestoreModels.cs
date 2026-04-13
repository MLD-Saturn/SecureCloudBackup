namespace AzureBackup.Core.Models;

/// <summary>
/// Result of a batch restore operation.
/// </summary>
public class RestoreResult
{
    public List<string> SuccessfulFiles { get; set; } = [];
    public List<string> FailedFiles { get; set; } = [];

    /// <summary>
    /// Files that failed normal restore but were partially recovered to a __corrupted__ subfolder.
    /// Each entry is (originalPath, corruptedPath, unrecoverableChunkCount).
    /// </summary>
    public List<(string OriginalPath, string RecoveredPath, int UnrecoverableChunks)> CorruptedRecoveryFiles { get; set; } = [];

    public long TotalBytesRestored { get; set; }

    public int TotalFilesProcessed => SuccessfulFiles.Count + FailedFiles.Count + CorruptedRecoveryFiles.Count;
    public bool IsSuccess => FailedFiles.Count == 0 && CorruptedRecoveryFiles.Count == 0;
}

/// <summary>
/// Progress event arguments for backup operations.
/// </summary>
public class BackupProgressEventArgs : EventArgs
{
    public string FilePath { get; set; } = string.Empty;
    public long BytesUploaded { get; set; }
    public int ChunksUploaded { get; set; }
    public int TotalChunks { get; set; }
}

/// <summary>
/// Statistics about the backup state.
/// </summary>
public class BackupStatistics
{
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public int CompletedFiles { get; set; }
    public int PendingFiles { get; set; }
    public int FailedFiles { get; set; }
    public int PendingChanges { get; set; }
    public DateTime? LastBackupTime { get; set; }
    public long TotalBytesUploaded { get; set; }

    public string TotalSizeFormatted => FormatHelper.FormatBytes(TotalSize);
    public string TotalBytesUploadedFormatted => FormatHelper.FormatBytes(TotalBytesUploaded);
}

/// <summary>
/// Simple key-value storage for index metadata timestamps.
/// </summary>
public class IndexMetadata
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public DateTime Value { get; set; }
}
