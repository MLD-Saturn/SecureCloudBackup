using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureBackup.ViewModels;

/// <summary>
/// ViewModel for a file tree node, supporting hierarchical selection and path remapping.
/// Represents Azure-backed files in the restore/sync tree.
/// </summary>
public partial class FileTreeNodeViewModel : TreeNodeViewModelBase<FileTreeNodeViewModel>
{
    /// <summary>
    /// Static event raised when any Azure file tree node's selection state changes.
    /// Used to notify the main view model to update selection-dependent UI state.
    /// </summary>
    public static event EventHandler? SelectionChanged;

    private readonly FileTreeNode _model;

    /// <inheritdoc />
    protected override void OnSelectionPropagationComplete()
        => SelectionChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The underlying model.
    /// </summary>
    public FileTreeNode Model => _model;

    /// <inheritdoc />
    public override string Name => _model.Name;

    /// <inheritdoc />
    public override string FullPath => _model.FullPath;

    /// <inheritdoc />
    public override bool IsFolder => _model.IsFolder;

    /// <summary>
    /// The backed up file (null for folders).
    /// </summary>
    public BackedUpFile? File => _model.File;

    /// <summary>
    /// Storage tier of this file (null for folders or if unknown).
    /// </summary>
    public StorageTier? StorageTier => _model.File?.CurrentStorageTier;

    /// <summary>
    /// Storage tier display text.
    /// </summary>
    public string StorageTierText => _model.File?.CurrentStorageTier?.ToString() ?? "";

    /// <summary>
    /// Whether the storage tier is known (for UI visibility).
    /// </summary>
    public bool HasStorageTier => _model.File?.CurrentStorageTier.HasValue == true;

    /// <summary>
    /// Custom restore path override for this node.
    /// When set, this path replaces the original path during restore.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveRestorePath))]
    [NotifyPropertyChangedFor(nameof(HasCustomRestorePath))]
    private string? _customRestorePath;

    /// <summary>
    /// True if this node has a custom restore path set.
    /// </summary>
    public bool HasCustomRestorePath => !string.IsNullOrEmpty(CustomRestorePath);

    /// <summary>
    /// The effective restore path, considering parent overrides.
    /// </summary>
    public string EffectiveRestorePath
    {
        get
        {
            if (HasCustomRestorePath)
                return CustomRestorePath!;

            var current = Parent;
            while (current != null)
            {
                if (current.HasCustomRestorePath)
                {
                    var relativePath = Path.GetRelativePath(current.FullPath, FullPath);
                    return Path.Combine(current.CustomRestorePath!, relativePath);
                }
                current = current.Parent;
            }

            return FullPath;
        }
    }

    /// <summary>
    /// Display string showing aggregate info for folders.
    /// </summary>
    public string AggregateInfo
    {
        get
        {
            if (!IsFolder)
                return FormatHelper.FormatBytes(_model.File?.FileSize ?? 0);

            var fileCount = _model.FileCount;
            var folderCount = _model.FolderCount;
            var totalSize = _model.TotalSize;

            List<string> parts = new();
            if (fileCount > 0)
                parts.Add($"{fileCount} file{(fileCount != 1 ? "s" : "")}");
            if (folderCount > 0)
                parts.Add($"{folderCount} folder{(folderCount != 1 ? "s" : "")}");
            parts.Add(FormatHelper.FormatBytes(totalSize));

            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// File size formatted for display.
    /// </summary>
    public string FileSizeText => IsFile ? FormatHelper.FormatBytes(_model.File?.FileSize ?? 0) : string.Empty;

    /// <summary>
    /// Last modified date for files.
    /// </summary>
    public string LastModifiedText => IsFile && _model.File != null 
        ? _model.File.LastModified.ToString("g") 
        : string.Empty;

    /// <summary>
    /// Icon indicator based on node type.
    /// </summary>
    public string Icon => IsFolder ? "[Dir]" : "[Cloud]";

    public FileTreeNodeViewModel(FileTreeNode model, FileTreeNodeViewModel? parent = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
        Parent = parent;

        foreach (var childModel in model.Children.OrderByDescending(c => c.IsFolder).ThenBy(c => c.Name))
        {
            FileTreeNodeViewModel childVm = new(childModel, this);
            Children.Add(childVm);
        }
    }

    /// <summary>
    /// Gets all selected file nodes in this subtree.
    /// </summary>
    public IEnumerable<FileTreeNodeViewModel> GetSelectedFiles()
    {
        if (IsFile && IsSelected)
        {
            yield return this;
        }

        foreach (var child in Children)
        {
            foreach (var selected in child.GetSelectedFiles())
            {
                yield return selected;
            }
        }
    }

    /// <summary>
    /// Gets all nodes in this subtree.
    /// </summary>
    public IEnumerable<FileTreeNodeViewModel> GetAllDescendants()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var descendant in child.GetAllDescendants())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Sets custom restore path for this node and notifies all descendants.
    /// </summary>
    public void SetCustomRestorePathAndNotify(string? path)
    {
        CustomRestorePath = path;

        foreach (var descendant in GetAllDescendants().Skip(1))
        {
            descendant.OnPropertyChanged(nameof(EffectiveRestorePath));
        }
    }

    /// <summary>
    /// Clears custom restore path from this node and all descendants.
    /// </summary>
    public void ClearCustomRestorePathRecursive()
    {
        CustomRestorePath = null;
        foreach (var child in Children)
        {
            child.ClearCustomRestorePathRecursive();
        }
    }

    /// <summary>
    /// Expands this node and all ancestors.
    /// </summary>
    public void ExpandToRoot()
    {
        IsExpanded = true;
        Parent?.ExpandToRoot();
    }

    /// <summary>
    /// Builds a tree structure from a flat list of backed up files.
    /// </summary>
    public static List<FileTreeNodeViewModel> BuildTree(IEnumerable<BackedUpFile> files)
    {
        Dictionary<string, FileTreeNode> rootNodes = new();

        foreach (var file in files)
        {
            var pathParts = file.LocalPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var rootName = pathParts[0];
            if (!rootName.EndsWith(':'))
                rootName = pathParts[0];
            else
                rootName = rootName + Path.DirectorySeparatorChar;

            if (!rootNodes.TryGetValue(rootName, out var rootNode))
            {
                rootNode = new FileTreeNode
                {
                    Name = rootName,
                    FullPath = rootName,
                    IsFolder = true
                };
                rootNodes[rootName] = rootNode;
            }

            var currentNode = rootNode;
            var currentPath = rootName;

            for (var i = 1; i < pathParts.Length; i++)
            {
                var part = pathParts[i];
                if (string.IsNullOrEmpty(part))
                    continue;

                currentPath = Path.Combine(currentPath, part);
                var isLastPart = i == pathParts.Length - 1;

                var existingChild = currentNode.Children.FirstOrDefault(c => 
                    c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));

                if (existingChild != null)
                {
                    currentNode = existingChild;
                }
                else
                {
                    FileTreeNode newNode = new()
                    {
                        Name = part,
                        FullPath = currentPath,
                        IsFolder = !isLastPart,
                        File = isLastPart ? file : null,
                        Parent = currentNode
                    };
                    currentNode.Children.Add(newNode);
                    currentNode = newNode;
                }
            }
        }

        return rootNodes.Values
            .OrderBy(n => n.Name)
            .Select(n => new FileTreeNodeViewModel(n))
            .ToList();
    }
}
