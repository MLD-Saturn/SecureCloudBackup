using System;
using System.Linq;
using AzureBackup.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureBackup.ViewModels;

/// <summary>
/// ViewModel wrapper for a watched folder configuration.
/// </summary>
public partial class WatchedFolderViewModel : ObservableObject
{
    /// <summary>
    /// The folder path to watch.
    /// </summary>
    [ObservableProperty]
    private string _path = string.Empty;

    /// <summary>
    /// Whether this folder is enabled for backup.
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>
    /// Semicolon-separated exclusion patterns (e.g., "*.tmp;*.log;node_modules").
    /// </summary>
    [ObservableProperty]
    private string _excludePatterns = string.Empty;

    public WatchedFolderViewModel(WatchedFolder model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _path = model.Path ?? string.Empty;
        _isEnabled = model.IsEnabled;
        _excludePatterns = model.ExcludePatterns != null 
            ? string.Join(";", model.ExcludePatterns) 
            : string.Empty;
    }

    /// <summary>
    /// Converts this ViewModel back to a model instance.
    /// </summary>
    public WatchedFolder ToModel() => new()
    {
        Path = Path ?? string.Empty,
        IsEnabled = IsEnabled,
        ExcludePatterns = (ExcludePatterns ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .ToList()
    };
}
