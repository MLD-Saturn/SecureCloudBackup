using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// ViewModel for the Tier Migration tab.
/// Displays backed-up files grouped into four panes by storage tier (Hot, Cool, Cold, Archive).
/// Supports drag-and-drop between panes to change a file's storage tier.
/// </summary>
public partial class TierMigrationViewModel : ViewModelBase
{
    private readonly IBlobStorageService _blobService;
    private readonly ChunkIndexService _chunkIndexService;
    private readonly Action<string> _log;
    private CancellationTokenSource? _operationCts;

    #region Observable Properties

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunOperations))]
    private bool _isOperationInProgress;

    [ObservableProperty]
    private string _statusMessage = "Load files to view storage tier distribution.";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    /// <summary>
    /// Files currently stored in the Hot tier.
    /// </summary>
    public ObservableCollection<BackedUpFileViewModel> HotFiles { get; } = [];

    /// <summary>
    /// Files currently stored in the Cool tier.
    /// </summary>
    public ObservableCollection<BackedUpFileViewModel> CoolFiles { get; } = [];

    /// <summary>
    /// Files currently stored in the Cold tier.
    /// </summary>
    public ObservableCollection<BackedUpFileViewModel> ColdFiles { get; } = [];

    /// <summary>
    /// Files currently stored in the Archive tier.
    /// </summary>
    public ObservableCollection<BackedUpFileViewModel> ArchiveFiles { get; } = [];

    public bool CanRunOperations => !IsOperationInProgress;

    [ObservableProperty]
    private string _hotSummary = "0 files";

    [ObservableProperty]
    private string _coolSummary = "0 files";

    [ObservableProperty]
    private string _coldSummary = "0 files";

    [ObservableProperty]
    private string _archiveSummary = "0 files";

    #endregion

    public TierMigrationViewModel(
        IBlobStorageService blobService,
        ChunkIndexService chunkIndexService,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(blobService);
        ArgumentNullException.ThrowIfNull(chunkIndexService);
        _blobService = blobService;
        _chunkIndexService = chunkIndexService;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Forwards a log message through the log delegate.
    /// Compiled out in Release builds when DIAGNOSTICLOG is not defined.
    /// </summary>
    [Conditional("DIAGNOSTICLOG")]
    private void Log(string message) => _log(message);

    /// <summary>
    /// Forwards a log message through the log delegate.
    /// Used by the View code-behind for drag-drop event logging.
    /// </summary>
    [Conditional("DIAGNOSTICLOG")]
    public void LogMessage(string message) => _log(message);

    /// <summary>
    /// Loads all backed-up file metadata from Azure and groups them by storage tier.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOperations))]
    private async Task LoadFilesAsync()
    {
        Log("LoadFilesAsync: Starting load of file metadata from Azure");
        IsOperationInProgress = true;
        StatusMessage = "Loading file metadata from Azure...";
        ProgressValue = 0;
        ProgressText = string.Empty;

        try
        {
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            var ct = _operationCts.Token;

            var progress = new Progress<(int completed, int total)>(p =>
            {
                ProgressValue = p.total > 0 ? (double)p.completed / p.total * 100 : 0;
                ProgressText = $"{p.completed}/{p.total}";
            });

            var files = await _blobService.LoadAllFileMetadataAsync(progress, cancellationToken: ct);
            Log($"LoadFilesAsync: Downloaded {files.Count} metadata entries");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HotFiles.Clear();
                CoolFiles.Clear();
                ColdFiles.Clear();
                ArchiveFiles.Clear();

                foreach (var file in files.OrderBy(f => f.LocalPath))
                {
                    var vm = new BackedUpFileViewModel(file);
                    var tier = file.CurrentStorageTier ?? StorageTier.Hot;
                    var collection = GetCollectionForTier(tier);
                    collection.Add(vm);
                }

                UpdateSummaries();
            });

            Log($"LoadFilesAsync: Completed — Hot={HotFiles.Count}, Cool={CoolFiles.Count}, Cold={ColdFiles.Count}, Archive={ArchiveFiles.Count}");
            StatusMessage = $"Loaded {files.Count} files.";
        }
        catch (OperationCanceledException)
        {
            Log("LoadFilesAsync: Cancelled by user");
            StatusMessage = "Load cancelled.";
        }
        catch (Exception ex)
        {
            Log($"LoadFilesAsync: ERROR {ex.GetType().Name}: {ex.Message}");
            StatusMessage = $"Error loading files: {ex.Message}";
        }
        finally
        {
            IsOperationInProgress = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    /// <summary>
    /// Migrates selected files from one tier to another.
    /// Called by the code-behind after a drag-drop completes.
    /// </summary>
    public async Task MigrateSelectedAsync(StorageTier sourceTier, StorageTier targetTier)
    {
        if (sourceTier == targetTier)
        {
            Log($"MigrateSelectedAsync: Source and target tier are both {sourceTier}, nothing to do");
            return;
        }

        var sourceCollection = GetCollectionForTier(sourceTier);
        var selected = sourceCollection.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Log($"MigrateSelectedAsync: No files selected in {sourceTier} pane, nothing to migrate");
            return;
        }

        Log($"MigrateSelectedAsync: Starting migration of {selected.Count} file(s) from {sourceTier} to {targetTier}");
        IsOperationInProgress = true;
        StatusMessage = $"Migrating {selected.Count} file(s) from {sourceTier} to {targetTier}...";

        try
        {
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            var ct = _operationCts.Token;

            var targetCollection = GetCollectionForTier(targetTier);
            var completed = 0;

            foreach (var fileVm in selected)
            {
                ct.ThrowIfCancellationRequested();
                Log($"MigrateSelectedAsync: [{completed + 1}/{selected.Count}] Processing '{fileVm.LocalPath}' ({fileVm.Model.Chunks.Count} chunks)");

                // Change tier on each chunk blob
                for (var i = 0; i < fileVm.Model.Chunks.Count; i++)
                {
                    var chunk = fileVm.Model.Chunks[i];
                    var blobName = string.IsNullOrEmpty(chunk.BlobName) ? $"chunks/{chunk.Hash}" : chunk.BlobName;
                    Log($"MigrateSelectedAsync: Setting chunk [{i + 1}/{fileVm.Model.Chunks.Count}] '{blobName}' to {targetTier}");
                    await _blobService.SetBlobTierAsync(blobName, targetTier, ct);
                }

                // Re-upload metadata at new tier (overwrites existing blob)
                Log($"MigrateSelectedAsync: Re-uploading metadata for '{fileVm.LocalPath}' at {targetTier} tier");
                await _blobService.UploadFileMetadataAsync(fileVm.Model, targetTier, ct);

                // Update the in-memory model
                fileVm.Model.CurrentStorageTier = targetTier;

                completed++;
                ProgressValue = (double)completed / selected.Count * 100;
                ProgressText = $"{completed}/{selected.Count}";
                StatusMessage = $"Migrated {completed}/{selected.Count} files to {targetTier}...";
            }

            // Move items between collections on the UI thread
            Log($"MigrateSelectedAsync: Moving {selected.Count} items from {sourceTier} to {targetTier} collection");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var fileVm in selected)
                {
                    fileVm.IsSelected = false;
                    sourceCollection.Remove(fileVm);
                    targetCollection.Add(fileVm);
                }

                UpdateSummaries();
            });

            Log($"MigrateSelectedAsync: Completed — {completed} file(s) migrated from {sourceTier} to {targetTier}");
            StatusMessage = $"Migrated {completed} file(s) from {sourceTier} to {targetTier}.";
        }
        catch (OperationCanceledException)
        {
            Log("MigrateSelectedAsync: Cancelled by user");
            StatusMessage = "Migration cancelled.";
        }
        catch (Exception ex)
        {
            Log($"MigrateSelectedAsync: ERROR {ex.GetType().Name}: {ex.Message}");
            StatusMessage = $"Migration error: {ex.Message}";
        }
        finally
        {
            IsOperationInProgress = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    /// <summary>
    /// Cancels any in-progress operation.
    /// </summary>
    [RelayCommand]
    private void CancelOperation()
    {
        Log("CancelOperation: Cancellation requested");
        _operationCts?.Cancel();
    }

    /// <summary>
    /// Selects all files in the specified tier pane.
    /// </summary>
    public void SelectAll(StorageTier tier)
    {
        var collection = GetCollectionForTier(tier);
        Log($"SelectAll: Selecting all {collection.Count} files in {tier} pane");
        foreach (var f in collection)
            f.IsSelected = true;
    }

    /// <summary>
    /// Deselects all files in the specified tier pane.
    /// </summary>
    public void DeselectAll(StorageTier tier)
    {
        var collection = GetCollectionForTier(tier);
        Log($"DeselectAll: Deselecting all {collection.Count} files in {tier} pane");
        foreach (var f in collection)
            f.IsSelected = false;
    }

    /// <summary>
    /// Deselects all files across every tier pane.
    /// </summary>
    [RelayCommand]
    private void DeselectAllFiles()
    {
        Log("DeselectAllFiles: Deselecting all files across all tiers");
        foreach (var tier in Enum.GetValues<StorageTier>())
            DeselectAll(tier);
    }

    /// <summary>
    /// Returns the observable collection for the given tier.
    /// </summary>
    public ObservableCollection<BackedUpFileViewModel> GetCollectionForTier(StorageTier tier) => tier switch
    {
        StorageTier.Hot => HotFiles,
        StorageTier.Cool => CoolFiles,
        StorageTier.Cold => ColdFiles,
        StorageTier.Archive => ArchiveFiles,
        _ => HotFiles
    };

    private void UpdateSummaries()
    {
        HotSummary = FormatSummary(HotFiles);
        CoolSummary = FormatSummary(CoolFiles);
        ColdSummary = FormatSummary(ColdFiles);
        ArchiveSummary = FormatSummary(ArchiveFiles);
    }

    private static string FormatSummary(ObservableCollection<BackedUpFileViewModel> files)
    {
        var totalBytes = files.Sum(f => f.Model.FileSize);
        return $"{files.Count} file(s), {FormatHelper.FormatBytes(totalBytes)}";
    }
}
