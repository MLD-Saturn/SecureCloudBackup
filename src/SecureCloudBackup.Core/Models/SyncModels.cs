namespace SecureCloudBackup.Core.Models;

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

    /// <summary>Files that were recovered to a __corrupted__ subfolder due to integrity errors.</summary>
    public int FilesCorruptedRecovered { get; set; }

    /// <summary>Total bytes transferred.</summary>
    public long BytesTransferred { get; set; }

    /// <summary>Error messages for failed files.</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>Paths of files recovered to __corrupted__ subfolders.</summary>
    public List<string> CorruptedRecoveryPaths { get; set; } = [];
}
