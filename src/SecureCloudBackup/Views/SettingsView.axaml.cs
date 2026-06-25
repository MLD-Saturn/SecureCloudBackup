using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SecureCloudBackup.ViewModels;

namespace SecureCloudBackup.Views;

public partial class SettingsView : UserControl
{
    private MainWindowViewModel? _wiredVm;

    public SettingsView()
    {
        InitializeComponent();

        // The DataContext is assigned by Avalonia after the constructor
        // runs, so subscribing here would race the binding. Defer until
        // DataContextChanged fires.
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => UnwireViewModel();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnwireViewModel();

        if (DataContext is MainWindowViewModel vm)
        {
            vm.RebuildQuarantinedDbPickerRequested += OnRebuildQuarantinedDbPickerRequested;
            _wiredVm = vm;
        }
    }

    private void UnwireViewModel()
    {
        if (_wiredVm is null) return;
        _wiredVm.RebuildQuarantinedDbPickerRequested -= OnRebuildQuarantinedDbPickerRequested;
        _wiredVm = null;
    }

    private async void OnRebuildQuarantinedDbPickerRequested(object? sender, EventArgs e)
        => await PickRebuildDbPathAsync();

    /// <summary>
    /// B61: OS-file-picker handler for the rebuild form's quarantined-catalog
    /// field. Defaults to
    /// <see cref="MainWindowViewModel.GetRebuildPickerStartDirectory"/>
    /// (which prefers the directory of whatever the user already typed,
    /// falling back to the data directory). Filters the picker so the
    /// quarantine-suffixed files float to the top, but still allows
    /// "All files" so users who copied a quarantined file under a
    /// different name can still select it.
    /// </summary>
    private async System.Threading.Tasks.Task PickRebuildDbPathAsync()
    {
        // async void event handlers must never let an exception escape;
        // the wrapper logs and swallows so the UI process survives.
        try
        {
            if (DataContext is not MainWindowViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is not { } storage) return;

            IStorageFolder? startFolder = null;
            try
            {
                var startPath = vm.GetRebuildPickerStartDirectory();
                if (!string.IsNullOrEmpty(startPath))
                {
                    startFolder = await storage.TryGetFolderFromPathAsync(new Uri(startPath));
                }
            }
            catch
            {
                // TryGetFolderFromPathAsync can throw on UNC / mapped-drive
                // edge cases; falling back to no start location is fine.
            }

            var fileTypes = new List<FilePickerFileType>
            {
                new("Quarantined catalog files")
                {
                    Patterns = new[] { "backup.db.quarantine-*" }
                },
                FilePickerFileTypes.All,
            };

            var pick = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select the quarantined database file",
                AllowMultiple = false,
                SuggestedStartLocation = startFolder,
                FileTypeFilter = fileTypes,
            });

            if (pick.Count == 0) return;

            var uri = pick[0].Path;
            var path = uri.IsAbsoluteUri ? uri.LocalPath : uri.ToString();
            if (string.IsNullOrWhiteSpace(path)) return;

            vm.SetRebuildQuarantinedDbPath(path);
        }
        catch (Exception ex)
        {
            (DataContext as MainWindowViewModel)?.AddLogMessage(
                $"Quarantined file picker error: {ex.Message}");
        }
    }
}

