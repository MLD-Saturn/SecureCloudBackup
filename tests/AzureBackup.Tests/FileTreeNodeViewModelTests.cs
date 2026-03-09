using AzureBackup.Core.Models;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for FileTreeNode model functionality.
/// Note: FileTreeNodeViewModel tests would require reference to AzureBackup (UI) project.
/// These tests verify the core model behavior.
/// </summary>
public class FileTreeNodeTests
{
    #region FileTreeNode Tests

    [Fact]
    public void TotalSize_WithSingleFile_ReturnsFileSize()
    {
        // Arrange
        var node = new FileTreeNode
        {
            Name = "test.txt",
            FullPath = @"C:\test.txt",
            IsFolder = false,
            File = CreateBackedUpFile(@"C:\test.txt", 1024)
        };

        // Act & Assert
        Assert.Equal(1024, node.TotalSize);
    }

    [Fact]
    public void TotalSize_WithFolder_SumsChildSizes()
    {
        // Arrange
        var folder = new FileTreeNode
        {
            Name = "folder",
            FullPath = @"C:\folder",
            IsFolder = true,
            Children =
            [
                new FileTreeNode
                {
                    Name = "file1.txt",
                    FullPath = @"C:\folder\file1.txt",
                    IsFolder = false,
                    File = CreateBackedUpFile(@"C:\folder\file1.txt", 1024)
                },
                new FileTreeNode
                {
                    Name = "file2.txt",
                    FullPath = @"C:\folder\file2.txt",
                    IsFolder = false,
                    File = CreateBackedUpFile(@"C:\folder\file2.txt", 2048)
                }
            ]
        };

        // Act & Assert
        Assert.Equal(3072, folder.TotalSize);
    }

    [Fact]
    public void FileCount_WithSingleFile_ReturnsOne()
    {
        // Arrange
        var node = new FileTreeNode
        {
            Name = "test.txt",
            IsFolder = false,
            File = CreateBackedUpFile(@"C:\test.txt", 1024)
        };

        // Act & Assert
        Assert.Equal(1, node.FileCount);
    }

    [Fact]
    public void FileCount_WithFolder_CountsAllFiles()
    {
        // Arrange
        var folder = new FileTreeNode
        {
            Name = "folder",
            IsFolder = true,
            Children =
            [
                new FileTreeNode { Name = "file1.txt", IsFolder = false, File = CreateBackedUpFile("f1", 100) },
                new FileTreeNode
                {
                    Name = "subfolder",
                    IsFolder = true,
                    Children =
                    [
                        new FileTreeNode { Name = "file2.txt", IsFolder = false, File = CreateBackedUpFile("f2", 100) },
                        new FileTreeNode { Name = "file3.txt", IsFolder = false, File = CreateBackedUpFile("f3", 100) }
                    ]
                }
            ]
        };

        // Act & Assert
        Assert.Equal(3, folder.FileCount);
    }

    [Fact]
    public void FolderCount_CountsAllSubfolders()
    {
        // Arrange
        var folder = new FileTreeNode
        {
            Name = "root",
            IsFolder = true,
            Children =
            [
                new FileTreeNode { Name = "file.txt", IsFolder = false },
                new FileTreeNode
                {
                    Name = "sub1",
                    IsFolder = true,
                    Children =
                    [
                        new FileTreeNode { Name = "sub2", IsFolder = true }
                    ]
                }
            ]
        };

        // Act & Assert
        Assert.Equal(2, folder.FolderCount); // sub1 and sub2
    }

    [Fact]
    public void GetAllFiles_ReturnsAllDescendantFiles()
    {
        // Arrange
        var file1 = CreateBackedUpFile("f1", 100);
        var file2 = CreateBackedUpFile("f2", 200);
        var folder = new FileTreeNode
        {
            Name = "root",
            IsFolder = true,
            Children =
            [
                new FileTreeNode { Name = "file1.txt", IsFolder = false, File = file1 },
                new FileTreeNode
                {
                    Name = "subfolder",
                    IsFolder = true,
                    Children =
                    [
                        new FileTreeNode { Name = "file2.txt", IsFolder = false, File = file2 }
                    ]
                }
            ]
        };

        // Act
        var files = folder.GetAllFiles().ToList();

        // Assert
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void GetAllDescendants_IncludesFoldersAndFiles()
    {
        // Arrange
        var folder = new FileTreeNode
        {
            Name = "root",
            IsFolder = true,
            Children =
            [
                new FileTreeNode { Name = "file.txt", IsFolder = false },
                new FileTreeNode { Name = "subfolder", IsFolder = true }
            ]
        };

        // Act
        var descendants = folder.GetAllDescendants().ToList();

        // Assert
        Assert.Equal(3, descendants.Count); // root + file + subfolder
    }

    #endregion

    #region MirrorSyncResult Tests

    [Fact]
    public void MirrorSyncResult_DefaultValues_AreZero()
    {
        // Arrange & Act
        var result = new MirrorSyncResult();

        // Assert
        Assert.Equal(0, result.FilesTransferred);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(0, result.FilesUnchanged);
        Assert.Equal(0, result.FilesErrored);
        Assert.Equal(0, result.BytesTransferred);
        Assert.Empty(result.Errors);
    }

    #endregion

    #region InitialSyncResult Tests

    [Fact]
    public void InitialSyncResult_TotalToBackup_SumsCorrectly()
    {
        // Arrange
        var result = new InitialSyncResult
        {
            NewFilesQueued = 5,
            ModifiedFilesQueued = 3,
            RetriedFiles = 2,
            AlreadyPending = 1
        };

        // Act & Assert
        Assert.Equal(11, result.TotalToBackup);
    }

    #endregion

    #region Helper Methods

    private static BackedUpFile CreateBackedUpFile(string path, long size)
    {
        return new BackedUpFile
        {
            LocalPath = path,
            FileSize = size,
            FileHash = Guid.NewGuid().ToString(),
            LastModified = DateTime.UtcNow,
            BackedUpAt = DateTime.UtcNow,
            Status = BackupStatus.Completed,
            Chunks = []
        };
    }

    #endregion
}
