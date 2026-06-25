using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SecureCloudBackup.ViewModels;

/// <summary>
/// Abstract base class for hierarchical tree node ViewModels with selection propagation,
/// expand/collapse, and parent-child management.
/// Uses CRTP (Curiously Recurring Template Pattern) so Children and Parent are correctly
/// typed in each derived class.
/// </summary>
/// <typeparam name="TSelf">The concrete derived type.</typeparam>
public abstract class TreeNodeViewModelBase<TSelf> : ObservableObject
    where TSelf : TreeNodeViewModelBase<TSelf>
{
    private TSelf? _parent;
    private bool _isUpdatingSelection;
    private bool _isExpanded;
    private bool _isSelected;

    /// <summary>
    /// Name of this node (file or folder name).
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Full path of this node.
    /// </summary>
    public abstract string FullPath { get; }

    /// <summary>
    /// True if this is a folder node.
    /// </summary>
    public abstract bool IsFolder { get; }

    /// <summary>
    /// True if this is a file node.
    /// </summary>
    public bool IsFile => !IsFolder;

    /// <summary>
    /// Child nodes.
    /// </summary>
    public ObservableCollection<TSelf> Children { get; } = [];

    /// <summary>
    /// Parent node (null for root).
    /// </summary>
    public TSelf? Parent
    {
        get => _parent;
        set => SetProperty(ref _parent, value);
    }

    /// <summary>
    /// Whether this node is expanded in the tree view.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                OnIsExpandedChanged(value);
        }
    }

    /// <summary>
    /// Whether this node is selected (checked).
    /// When a folder is selected, all children are also selected.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(IsPartiallySelected));
                OnIsSelectedChanged(value);
            }
        }
    }

    /// <summary>
    /// True if this folder has some but not all children selected.
    /// Used for showing indeterminate checkbox state.
    /// </summary>
    public bool IsPartiallySelected
    {
        get
        {
            if (!IsFolder || Children.Count == 0)
                return false;

            var selectedCount = Children.Count(c => c.IsSelected || c.IsPartiallySelected);
            return selectedCount > 0 && selectedCount < Children.Count;
        }
    }

    /// <summary>
    /// Called when IsSelected changes. Propagates selection to children and updates parent.
    /// </summary>
    private void OnIsSelectedChanged(bool value)
    {
        if (_isUpdatingSelection)
            return;

        _isUpdatingSelection = true;
        try
        {
            if (IsFolder)
            {
                foreach (var child in Children)
                {
                    child.SetSelectionRecursive(value);
                }
            }

            Parent?.OnChildSelectionChanged();
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        OnSelectionPropagationComplete();
    }

    /// <summary>
    /// Called when IsExpanded changes. Auto-expands single-child folder chains
    /// to improve navigation UX.
    /// </summary>
    private void OnIsExpandedChanged(bool value)
    {
        if (!value || !IsFolder)
            return;

        if (Children.Count == 1 && Children[0].IsFolder)
        {
            Children[0].IsExpanded = true;
        }
    }

    /// <summary>
    /// Called after selection propagation completes.
    /// Override in derived classes to fire static SelectionChanged events.
    /// </summary>
    protected virtual void OnSelectionPropagationComplete() { }

    /// <summary>
    /// Sets selection state recursively without triggering parent updates.
    /// </summary>
    private void SetSelectionRecursive(bool selected)
    {
        _isUpdatingSelection = true;
        try
        {
            IsSelected = selected;
            foreach (var child in Children)
            {
                child.SetSelectionRecursive(selected);
            }
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    /// <summary>
    /// Called when a child's selection changes.
    /// </summary>
    private void OnChildSelectionChanged()
    {
        OnPropertyChanged(nameof(IsPartiallySelected));

        _isUpdatingSelection = true;
        try
        {
            var allSelected = Children.All(c => c.IsSelected);
            var noneSelected = Children.All(c => !c.IsSelected && !c.IsPartiallySelected);

            if (allSelected)
                IsSelected = true;
            else if (noneSelected)
                IsSelected = false;
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        Parent?.OnChildSelectionChanged();
    }

    /// <summary>
    /// Expands all nodes in this subtree.
    /// </summary>
    public void ExpandAll()
    {
        IsExpanded = true;
        foreach (var child in Children)
        {
            child.ExpandAll();
        }
    }

    /// <summary>
    /// Collapses all nodes in this subtree.
    /// </summary>
    public void CollapseAll()
    {
        IsExpanded = false;
        foreach (var child in Children)
        {
            child.CollapseAll();
        }
    }
}
