namespace AzureBackup.Core.Models;

/// <summary>
/// Result of an initial sync operation.
/// </summary>
public class InitialSyncResult
{
    /// <summary>Total files found in watched folders.</summary>
    public int TotalFilesScanned { get; set; }
    
    /// <summary>New files that were queued for backup.</summary>
    public int NewFilesQueued { get; set; }
    
    /// <summary>Modified files that were queued for backup.</summary>
    public int ModifiedFilesQueued { get; set; }
    
    /// <summary>Previously failed files that were retried.</summary>
    public int RetriedFiles { get; set; }
    
    /// <summary>Files that haven't changed since last backup.</summary>
    public int UnchangedFiles { get; set; }
    
    /// <summary>Files that were already in the pending queue.</summary>
    public int AlreadyPending { get; set; }
    
    /// <summary>Files that were skipped (excluded status).</summary>
    public int SkippedFiles { get; set; }
    
    /// <summary>Files that had errors during checking.</summary>
    public int ErrorFiles { get; set; }
    
    /// <summary>Total files that need to be backed up.</summary>
    public int TotalToBackup => NewFilesQueued + ModifiedFilesQueued + RetriedFiles + AlreadyPending;
}

/// <summary>
/// Result of a mirror sync operation.
/// </summary>
public class MirrorSyncResult
{
    /// <summary>Files that were restored/backed up.</summary>
    public int FilesTransferred { get; set; }
    
    /// <summary>Files that were deleted to match the source.</summary>
    public int FilesDeleted { get; set; }
    
    /// <summary>Files that were unchanged.</summary>
    public int FilesUnchanged { get; set; }
    
    /// <summary>Files that had errors.</summary>
    public int FilesErrored { get; set; }
    
    /// <summary>Total bytes transferred.</summary>
    public long BytesTransferred { get; set; }
    
    /// <summary>Error messages for failed files.</summary>
    public List<string> Errors { get; set; } = [];
}

/// <summary>
/// Overall sync progress information.
/// </summary>
public class SyncProgress
{
    /// <summary>Total number of files to process.</summary>
    public int TotalFiles { get; set; }
    
    /// <summary>Number of files completed.</summary>
    public int CompletedFiles { get; set; }
    
    /// <summary>Number of files that failed.</summary>
    public int FailedFiles { get; set; }
    
    /// <summary>Number of files skipped (unchanged).</summary>
    public int SkippedFiles { get; set; }
    
    /// <summary>Files remaining to process.</summary>
    public int RemainingFiles => TotalFiles - CompletedFiles - FailedFiles - SkippedFiles;
    
    /// <summary>Total bytes to transfer.</summary>
    public long TotalBytes { get; set; }
    
    /// <summary>Bytes transferred so far.</summary>
    public long BytesTransferred { get; set; }
    
    /// <summary>Current transfer speed in bytes per second.</summary>
    public double BytesPerSecond { get; set; }
    
    /// <summary>Estimated time remaining in seconds.</summary>
    public double EstimatedSecondsRemaining => BytesPerSecond > 0 
        ? (TotalBytes - BytesTransferred) / BytesPerSecond 
        : 0;
    
    /// <summary>Overall progress percentage.</summary>
    public double ProgressPercent => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;
    
    /// <summary>Human-readable transfer speed.</summary>
    public string SpeedText => $"{FormatBytes((long)BytesPerSecond)}/s";
    
    /// <summary>Human-readable time remaining.</summary>
    public string TimeRemainingText
    {
        get
        {
            var seconds = EstimatedSecondsRemaining;
            if (seconds <= 0) return "Calculating...";
            if (seconds < 60) return $"{seconds:F0}s remaining";
            if (seconds < 3600) return $"{seconds / 60:F0}m {seconds % 60:F0}s remaining";
            return $"{seconds / 3600:F0}h {(seconds % 3600) / 60:F0}m remaining";
        }
    }
    
    /// <summary>Current file being processed.</summary>
    public FileTransferProgress? CurrentFile { get; set; }
    
    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
