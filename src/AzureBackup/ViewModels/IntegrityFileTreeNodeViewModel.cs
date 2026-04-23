using System.Collections.Generic;
using System.IO;
using System.Linq;
using AzureBackup.Core.Models;

namespace AzureBackup.ViewModels;

/// <summary>
/// Tree node for the Data Integrity tab's file-selection panel (D2).
/// Mirrors <see cref="LocalFileTreeNodeViewModel"/>'s shape (checkbox
/// propagation via <see cref="TreeNodeViewModelBase{T}"/>) but the leaf
/// nodes carry a <see cref="BackedUpFile"/> rather than a local file
/// because the integrity check operates on the backed-up corpus, not
/// the local filesystem.
/// </summary>
/// <remarks>
/// Static <see cref="SelectionChanged"/> event is the bridge from
/// checkbox-toggle to ViewModel: when the user manually toggles a
/// checkbox, <see cref="DataIntegrityViewModel"/> resets the
/// time/history dropdown to "(Custom selection)" -- the bidirectional
/// sync rule from the design discussion.
/// </remarks>
public partial class IntegrityFileTreeNodeViewModel : TreeNodeViewModelBase<IntegrityFileTreeNodeViewModel>
{
    public static event System.EventHandler? SelectionChanged;

    protected override void OnSelectionPropagationComplete()
        => SelectionChanged?.Invoke(this, System.EventArgs.Empty);

    public override string Name { get; }
    public override string FullPath { get; }
    public override bool IsFolder { get; }

    /// <summary>
    /// The persisted <see cref="BackedUpFile"/> this leaf represents.
    /// Null for folder nodes. The integrity check uses
    /// <c>File.Id</c> as the FileId.
    /// </summary>
    public BackedUpFile? File { get; }

    /// <summary>
    /// Backed-up timestamp for time-filter matching. Folders carry the
    /// most recent of their descendants so a folder selects-on-time-filter
    /// as soon as any child file matches.
    /// </summary>
    public System.DateTime BackedUpAt { get; }

    private IntegrityFileTreeNodeViewModel(string name, string fullPath, bool isFolder,
        BackedUpFile? file, System.DateTime backedUpAt)
    {
        Name = name;
        FullPath = fullPath;
        IsFolder = isFolder;
        File = file;
        BackedUpAt = backedUpAt;
    }

    /// <summary>
    /// Folder summary text shown next to the folder name. Counts files
    /// in the subtree to give the tester a sense of scope before they
    /// expand.
    /// </summary>
    public string FolderSummary
    {
        get
        {
            if (!IsFolder) return string.Empty;
            var fileCount = GetAllFiles().Count();
            return $"{fileCount} file{(fileCount == 1 ? "" : "s")}";
        }
    }

    public IEnumerable<IntegrityFileTreeNodeViewModel> GetAllFiles()
    {
        if (IsFile)
        {
            yield return this;
        }
        else
        {
            foreach (var child in Children)
                foreach (var f in child.GetAllFiles())
                    yield return f;
        }
    }

    /// <summary>
    /// Builds a directory-grouped tree from a flat list of backed-up files.
    /// Each file becomes a leaf; common path prefixes become folder nodes.
    /// </summary>
    /// <remarks>
    /// D7 review fix 3.5: O(N) instead of O(N^2). The pre-D7 implementation
    /// did a linear scan of <c>parent.Children</c> for each path component
    /// of every file -- a corpus where 1000 files share <c>C:\Photos\</c>
    /// did 1000 linear scans of an ever-growing list. We now keep a
    /// transient per-node dictionary keyed by case-insensitive folder
    /// name so each lookup is O(1). The dictionaries are dropped after
    /// the build; only the resulting Children lists survive (the runtime
    /// data structure the TreeView actually binds to).
    /// </remarks>
    public static List<IntegrityFileTreeNodeViewModel> BuildTree(IEnumerable<BackedUpFile> files)
    {
        var roots = new Dictionary<string, IntegrityFileTreeNodeViewModel>(System.StringComparer.OrdinalIgnoreCase);
        // Per-node child-folder lookup; absent for leaf nodes.
        var folderLookup = new Dictionary<IntegrityFileTreeNodeViewModel, Dictionary<string, IntegrityFileTreeNodeViewModel>>();

        foreach (var file in files.OrderBy(f => f.LocalPath, System.StringComparer.OrdinalIgnoreCase))
        {
            var parts = SplitPath(file.LocalPath);
            if (parts.Length == 0) continue;

            var rootName = parts[0];
            if (!roots.TryGetValue(rootName, out var root))
            {
                root = new IntegrityFileTreeNodeViewModel(rootName, rootName,
                    isFolder: true, file: null, backedUpAt: file.BackedUpAt);
                roots[rootName] = root;
                folderLookup[root] = new Dictionary<string, IntegrityFileTreeNodeViewModel>(System.StringComparer.OrdinalIgnoreCase);
            }

            InsertFile(root, parts, 1, file, folderLookup);
        }

        return roots.Values.OrderBy(r => r.Name, System.StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void InsertFile(IntegrityFileTreeNodeViewModel parent, string[] parts, int index,
        BackedUpFile file, Dictionary<IntegrityFileTreeNodeViewModel, Dictionary<string, IntegrityFileTreeNodeViewModel>> folderLookup)
    {
        if (index == parts.Length - 1)
        {
            // Leaf
            var leaf = new IntegrityFileTreeNodeViewModel(
                parts[index], file.LocalPath, isFolder: false, file: file, file.BackedUpAt)
            {
                Parent = parent
            };
            parent.Children.Add(leaf);
            return;
        }

        var folderName = parts[index];
        var siblings = folderLookup[parent];
        if (!siblings.TryGetValue(folderName, out var existing))
        {
            var folderPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts.Take(index + 1));
            existing = new IntegrityFileTreeNodeViewModel(folderName, folderPath,
                isFolder: true, file: null, file.BackedUpAt)
            {
                Parent = parent
            };
            parent.Children.Add(existing);
            siblings[folderName] = existing;
            folderLookup[existing] = new Dictionary<string, IntegrityFileTreeNodeViewModel>(System.StringComparer.OrdinalIgnoreCase);
        }
        InsertFile(existing, parts, index + 1, file, folderLookup);
    }

    private static string[] SplitPath(string path)
    {
        // Normalize separators then split. Drop empty segments from
        // leading separators. Keep the drive letter (e.g., "C:\\") as
        // a single root component.
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        var parts = normalized.Split(Path.DirectorySeparatorChar, System.StringSplitOptions.RemoveEmptyEntries);
        return parts;
    }
}
