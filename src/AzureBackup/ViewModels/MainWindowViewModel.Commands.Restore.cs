using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// Restore commands for MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{
    #region Restore Commands

    [RelayCommand]
    private async Task SearchFilesAsync()
    {
        if (!IsInitialized || string.IsNullOrWhiteSpace(SearchPattern))
            return;

        IsOperationInProgress = true;
        try
        {
            var files = await _restoreService.SearchFilesAsync(SearchPattern);
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RestorableFiles.Clear();
                foreach (var file in files)
                {
                    RestorableFiles.Add(new BackedUpFileViewModel(file));
                }
                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                OnPropertyChanged(nameof(ShowAzureEmptyState));
                
                // Rebuild tree if in tree view mode
                if (UseTreeView)
                {
                    BuildFileTree();
                }
            });
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedFileAsync()
    {
        if (!IsInitialized || SelectedRestoreFile == null)
        {
            AddLog("Please select a file to delete");
            return;
        }

        var fileToDelete = SelectedRestoreFile;
        var fileName = Path.GetFileName(fileToDelete.LocalPath);
        
        AddLog($"Deleting {fileName} from Azure...");
        IsOperationInProgress = true;

        try
        {
            var success = await _restoreService.DeleteFileAsync(fileToDelete.Model);
            
            if (success)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    RestorableFiles.Remove(fileToDelete);
                    OnPropertyChanged(nameof(RestorableFilesEmpty));
                    OnPropertyChanged(nameof(RestorableFilesCount));
                });
                AddLog($"Deleted {fileName} from Azure");
            }
            else
            {
                AddLog($"Failed to delete {fileName}");
            }
        }
        catch (Exception ex)
        {
            AddLog($"Error deleting {fileName}: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedFilesAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first");
            return;
        }

        // Get selected files - handle both tree view and flat list modes
        List<BackedUpFileViewModel> filesToDelete;
        if (UseTreeView)
        {
            // In tree view mode, first check for checked (checkbox) files
            var selectedTreeFiles = FileTreeRoots
                .SelectMany(r => r.GetSelectedFiles())
                .Where(f => f.File != null)
                .Select(f => RestorableFiles.FirstOrDefault(rf => rf.Model == f.File))
                .Where(f => f != null)
                .Cast<BackedUpFileViewModel>()
                .ToList();
            
            // If no checked files, use the right-clicked/selected tree node
            if (selectedTreeFiles.Count == 0 && SelectedTreeNode != null)
            {
                // Get all files from the selected node (works for both files and folders)
                var filesFromNode = SelectedTreeNode.GetAllDescendants()
                    .Where(n => n.IsFile && n.File != null)
                    .Select(n => RestorableFiles.FirstOrDefault(rf => rf.Model == n.File))
                    .Where(f => f != null)
                    .Cast<BackedUpFileViewModel>()
                    .ToList();
                selectedTreeFiles = filesFromNode;
            }
            
            filesToDelete = selectedTreeFiles;
        }
        else
        {
            filesToDelete = SelectedRestoreFiles.ToList();
        }
        
        if (filesToDelete.Count == 0)
        {
            AddLog("Please select files to delete");
            return;
        }

        // Generate preview
        var preview = _restoreService.PreviewDeleteFromAzure(filesToDelete.Select(f => f.Model));

        // Show preview dialog and get user confirmation
        var confirmed = await ShowPreviewDialogAsync(preview);

        if (!confirmed)
        {
            AddLog("Delete operation cancelled by user");
            return;
        }

        // Remove files the user excluded in the preview dialog
        var excluded = preview.ExcludedFilePaths;
        if (excluded.Count > 0)
        {
            filesToDelete = filesToDelete
                .Where(f => !excluded.Contains(f.Model.LocalPath))
                .ToList();
        }

        if (filesToDelete.Count == 0)
        {
            AddLog("All files were excluded - nothing to delete");
            return;
        }

        AddLog($"Deleting {filesToDelete.Count} file(s) from Azure...");
        IsOperationInProgress = true;
        CreateOperationCts();

        try
        {
            var totalBytes = filesToDelete.Sum(f => f.Model.FileSize);
            StartProgressTracking("Deleting", filesToDelete.Count, totalBytes);

            // Use parallel batch deletion — all blob deletes run concurrently
            var models = filesToDelete.Select(f => f.Model).ToList();

            var deleteProgress = new Progress<(int filesCompleted, int totalFiles, string currentFileName)>(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    CompletedFilesCount = p.filesCompleted;
                    ProgressValue = p.totalFiles > 0 ? (double)p.filesCompleted / p.totalFiles * 100 : 0;
                    ProgressText = $"Deleting: {p.currentFileName}";
                    OnPropertyChanged(nameof(FilesProgressText));
                });
            });

            var successfullyDeleted = await _restoreService.DeleteFilesAsync(
                models, deleteProgress, _operationCts!.Token);

            var failCount = filesToDelete.Count - successfullyDeleted.Count;

            // Single batched UI update — remove all successful deletions at once
            var deletedPaths = successfullyDeleted.Select(f => f.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Remove in reverse order to avoid index shifting during removal
                for (var i = RestorableFiles.Count - 1; i >= 0; i--)
                {
                    if (deletedPaths.Contains(RestorableFiles[i].LocalPath))
                    {
                        RestorableFiles.RemoveAt(i);
                    }
                }

                SelectedTreeNode = null;
                foreach (var file in RestorableFiles)
                {
                    file.IsSelected = false;
                }

                OnPropertyChanged(nameof(RestorableFilesEmpty));
                OnPropertyChanged(nameof(RestorableFilesCount));
                NotifySelectionChanged();

                if (UseTreeView)
                {
                    BuildFileTree();
                }
            });

            // Refresh local files to update backup status indicators
            await RefreshLocalFilesAsync();

            AddLog($"Delete complete: {successfullyDeleted.Count} succeeded, {failCount} failed");
        }
        catch (OperationCanceledException)
        {
            AddLog("Delete cancelled");
        }
        finally
        {
            IsOperationInProgress = false;
            StopProgressTracking();
        }
    }

    [RelayCommand]
    private void BrowseRestoreDirectory()
    {
        // Request the View to open a folder picker dialog for restore directory
        RestoreFolderPickerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the View after a restore folder is selected.
    /// </summary>
    public void SetRestoreDirectory(string folderPath)
    {
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            RestoreDirectory = folderPath;
            AddLog($"Restore directory set to: {folderPath}");
        }
    }

    #endregion
}
