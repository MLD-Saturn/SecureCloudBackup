namespace SecureCloudBackup.Core.Models;

/// <summary>
/// Represents a node in a file/folder tree structure for restore operations.
/// Note: TotalSize, FileCount, and FolderCount are computed recursively on each access.
/// For very large trees (10000+ files), consider caching these values if performance is an issue.
/// </summary>
public class FileTreeNode
{
    /// <summary>Name of this node (file or folder name only, not full path).</summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>Full path of this node.</summary>
    public string FullPath { get; set; } = string.Empty;
    
    /// <summary>True if this is a folder node, false if it's a file.</summary>
    public bool IsFolder { get; set; }
    
    /// <summary>The backed up file info (null for folder nodes).</summary>
    public BackedUpFile? File { get; set; }
    
    /// <summary>Child nodes (subfolders and files).</summary>
    public List<FileTreeNode> Children { get; set; } = [];
    
    /// <summary>Parent node (null for root nodes).</summary>
    public FileTreeNode? Parent { get; set; }
    
    /// <summary>Total size of all files in this node and descendants.</summary>
    public long TotalSize => IsFolder 
        ? Children.Sum(c => c.TotalSize) 
        : (File?.FileSize ?? 0);
    
    /// <summary>Total count of files in this node and descendants.</summary>
    public int FileCount => IsFolder 
        ? Children.Sum(c => c.FileCount) 
        : 1;
    
    /// <summary>Total count of folders in this node and descendants (excluding self).</summary>
    public int FolderCount => IsFolder 
        ? Children.Count(c => c.IsFolder) + Children.Sum(c => c.FolderCount) 
        : 0;
    
    /// <summary>
    /// Gets all descendant file nodes (recursive).
    /// </summary>
    public IEnumerable<FileTreeNode> GetAllFiles()
    {
        if (!IsFolder && File != null)
        {
            yield return this;
        }
        
        foreach (var child in Children)
        {
            foreach (var file in child.GetAllFiles())
            {
                yield return file;
            }
        }
    }
    
    /// <summary>
    /// Gets all descendant nodes (recursive), including folders.
    /// </summary>
    public IEnumerable<FileTreeNode> GetAllDescendants()
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
}
