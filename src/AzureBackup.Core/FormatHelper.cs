namespace AzureBackup.Core;

/// <summary>
/// Shared formatting utilities used across the application.
/// </summary>
public static class FormatHelper
{
    private static readonly string[] ByteSizes = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 GB").
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        var order = 0;
        double size = bytes;
        while (size >= 1024 && order < ByteSizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {ByteSizes[order]}";
    }
}
