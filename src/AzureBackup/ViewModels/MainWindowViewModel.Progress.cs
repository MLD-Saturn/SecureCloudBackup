using System;
using AzureBackup.Core;

namespace AzureBackup.ViewModels;

/// <summary>
/// Progress tracking helpers for file transfer operations.
/// </summary>
public partial class MainWindowViewModel
{
    private readonly SpeedTracker _legacySpeedTracker = new();

    /// <summary>
    /// Starts a new operation with progress tracking.
    /// </summary>
    private void StartProgressTracking(string operationType, int totalFiles, long totalBytes)
    {
        _legacySpeedTracker.Start();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsTransferInProgress = true; // Show the progress panel
            CurrentOperationType = operationType;
            TotalFilesInOperation = totalFiles;
            TotalBytesToProcess = totalBytes;
            CompletedFilesCount = 0;
            TotalBytesProcessed = 0;
            ProgressValue = 0;
            OperationSpeed = string.Empty;
            EstimatedTimeRemaining = string.Empty;
            _currentFileIndex = 0;
            
            OnPropertyChanged(nameof(BytesProgressText));
            OnPropertyChanged(nameof(FilesProgressText));
        });
    }

    /// <summary>
    /// Updates overall progress using a pre-computed aggregate byte total.
    /// Used by parallel operations where the service layer tracks aggregate bytes via Interlocked.
    /// Avoids the single-file recomputation that causes jumps during parallel processing.
    /// </summary>
    private void UpdateOverallProgress(long aggregateBytesProcessed, int completedFiles)
    {
        TotalBytesProcessed = aggregateBytesProcessed;
        CompletedFilesCount = completedFiles;
        ProgressValue = TotalBytesToProcess > 0
            ? (double)TotalBytesProcessed / TotalBytesToProcess * 100
            : 0;

        OnPropertyChanged(nameof(BytesProgressText));
        OnPropertyChanged(nameof(FilesProgressText));
        UpdateSpeedAndEta();
    }

    /// <summary>
    /// Updates speed calculation and estimated time remaining using the shared <see cref="SpeedTracker"/>.
    /// </summary>
    private void UpdateSpeedAndEta()
    {
        if (!_legacySpeedTracker.Update(TotalBytesProcessed, TotalBytesToProcess))
            return;

        OperationSpeed = _legacySpeedTracker.Speed;
        EstimatedTimeRemaining = _legacySpeedTracker.EstimatedTimeRemaining;
    }

    /// <summary>
    /// Clears progress tracking state after operation completes.
    /// </summary>
    private void ClearProgressTracking()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ProgressValue = 0;
            ProgressText = string.Empty;
            CurrentOperationType = string.Empty;
            CompletedFilesCount = 0;
            TotalFilesInOperation = 0;
            TotalBytesProcessed = 0;
            TotalBytesToProcess = 0;
            OperationSpeed = string.Empty;
            EstimatedTimeRemaining = string.Empty;
            _currentFileIndex = 0;
            
            OnPropertyChanged(nameof(BytesProgressText));
            OnPropertyChanged(nameof(FilesProgressText));
        });
    }

    /// <summary>
    /// Stops progress tracking and hides the progress panel.
    /// Call this at the end of file transfer operations.
    /// </summary>
    private void StopProgressTracking()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsTransferInProgress = false;
            ClearProgressTracking();
        });
    }
}
