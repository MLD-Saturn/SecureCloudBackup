namespace AzureBackup.Core.Models;

/// <summary>
/// Detailed progress information for an individual file during sync.
/// </summary>
public class FileTransferProgress
{
    /// <summary>File path being transferred.</summary>
    public string FilePath { get; set; } = string.Empty;
    
    /// <summary>File name for display.</summary>
    public string FileName => Path.GetFileName(FilePath);
    
    /// <summary>Total file size in bytes.</summary>
    public long TotalBytes { get; set; }
    
    /// <summary>Bytes transferred so far.</summary>
    public long BytesTransferred { get; set; }
    
    /// <summary>Transfer direction.</summary>
    public TransferDirection Direction { get; set; }
    
    /// <summary>Current transfer status.</summary>
    public TransferStatus Status { get; set; }
    
    /// <summary>Progress percentage (0-100).</summary>
    public double ProgressPercent => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes * 100 : 0;
    
    /// <summary>Human-readable progress text.</summary>
    public string ProgressText => $"{FormatBytes(BytesTransferred)} / {FormatBytes(TotalBytes)}";
    
    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; set; }
    
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

/// <summary>
/// Direction of file transfer.
/// </summary>
public enum TransferDirection
{
    /// <summary>Uploading to Azure (backup).</summary>
    Upload,
    
    /// <summary>Downloading from Azure (restore).</summary>
    Download
}

/// <summary>
/// Status of a file transfer.
/// </summary>
public enum TransferStatus
{
    /// <summary>Waiting to start.</summary>
    Pending,
    
    /// <summary>Currently transferring.</summary>
    InProgress,
    
    /// <summary>Transfer completed successfully.</summary>
    Completed,
    
    /// <summary>Transfer failed.</summary>
    Failed,
    
    /// <summary>Transfer was skipped (file unchanged).</summary>
    Skipped
}
