using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for PathHelper utilities.
/// Uses platform-appropriate path separators via Path.Combine to work on Windows and Unix.
/// </summary>
public class PathHelperTests
{
    #region GetRelativePathFromBase

    [Fact]
    public void WhenFileUnderBaseThenReturnsRelativePath()
    {
        var basePath = Path.Combine("C:", "Users", "me", "Documents");
        var fullPath = Path.Combine("C:", "Users", "me", "Documents", "sub", "file.txt");

        var result = PathHelper.GetRelativePathFromBase(fullPath, basePath);

        Assert.Equal(Path.Combine("sub", "file.txt"), result);
    }

    [Fact]
    public void WhenFileDirectlyInBaseThenReturnsFilename()
    {
        var basePath = Path.Combine("C:", "Users", "me", "Documents");
        var fullPath = Path.Combine("C:", "Users", "me", "Documents", "file.txt");

        var result = PathHelper.GetRelativePathFromBase(fullPath, basePath);

        Assert.Equal("file.txt", result);
    }

    [Fact]
    public void WhenFileNotUnderBaseThenFallsBackToFilename()
    {
        var basePath = Path.Combine("C:", "Users", "me", "Documents");
        var fullPath = Path.Combine("D:", "Other", "file.txt");

        var result = PathHelper.GetRelativePathFromBase(fullPath, basePath);

        Assert.Equal("file.txt", result);
    }

    [Fact]
    public void WhenBaseHasTrailingSeparatorThenStillWorks()
    {
        var basePath = Path.Combine("C:", "Users", "me", "Documents") + Path.DirectorySeparatorChar;
        var fullPath = Path.Combine("C:", "Users", "me", "Documents", "file.txt");

        var result = PathHelper.GetRelativePathFromBase(fullPath, basePath);

        Assert.Equal("file.txt", result);
    }

    #endregion

    #region FindCommonRoot

    [Fact]
    public void WhenAllPathsShareRootThenReturnsCommonDirectory()
    {
        var paths = new[]
        {
            Path.Combine("C:", "Users", "me", "Docs", "a.txt"),
            Path.Combine("C:", "Users", "me", "Docs", "b.txt"),
            Path.Combine("C:", "Users", "me", "Docs", "sub", "c.txt")
        };

        var result = PathHelper.FindCommonRoot(paths);

        Assert.Equal(Path.Combine("C:", "Users", "me", "Docs"), result);
    }

    [Fact]
    public void WhenPathsInDifferentSubfoldersThenReturnsParent()
    {
        var paths = new[]
        {
            Path.Combine("C:", "Users", "me", "Docs", "a.txt"),
            Path.Combine("C:", "Users", "me", "Photos", "b.jpg")
        };

        var result = PathHelper.FindCommonRoot(paths);

        Assert.Equal(Path.Combine("C:", "Users", "me"), result);
    }

    [Fact]
    public void WhenEmptyListThenReturnsEmpty()
    {
        var result = PathHelper.FindCommonRoot(Array.Empty<string>());
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void WhenSinglePathThenReturnsItsDirectory()
    {
        var paths = new[] { Path.Combine("C:", "Users", "me", "file.txt") };

        var result = PathHelper.FindCommonRoot(paths);

        Assert.Equal(Path.Combine("C:", "Users", "me"), result);
    }

    [Fact]
    public void WhenAllPathsAreBareFilenamesThenReturnsEmpty()
    {
        // Bare filenames have no directory component, so there is no
        // common root to compute. Path.GetDirectoryName returns "" for
        // each, they are filtered out, and the helper returns empty.
        var paths = new[] { "a.txt", "b.txt" };

        var result = PathHelper.FindCommonRoot(paths);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void WhenPathsHaveNoSharedRootThenReturnsEmpty()
    {
        // Two distinct roots share no prefix. The helper walks the
        // candidate root up its parent chain; when the parent chain is
        // exhausted without a match it returns empty rather than a
        // bogus partial root.
        var paths = new[]
        {
            Path.Combine("C:", "alpha", "a.txt"),
            Path.Combine("D:", "beta", "b.txt")
        };

        var result = PathHelper.FindCommonRoot(paths);

        Assert.Equal(string.Empty, result);
    }

    #endregion

    #region GetDisplayName

    [Theory]
    [InlineData("Documents", "Documents")]
    [InlineData("my-folder", "my-folder")]
    public void WhenSimpleFolderNameThenReturnsName(string input, string expected)
    {
        Assert.Equal(expected, PathHelper.GetDisplayName(input));
    }

    [Fact]
    public void WhenNestedPathThenReturnsLastSegment()
    {
        var path = Path.Combine("C:", "Users", "me", "Documents");
        Assert.Equal("Documents", PathHelper.GetDisplayName(path));
    }

    [Fact]
    public void WhenPathHasTrailingSeparatorThenReturnsLastSegment()
    {
        var path = Path.Combine("C:", "Users", "me", "Documents") + Path.DirectorySeparatorChar;
        Assert.Equal("Documents", PathHelper.GetDisplayName(path));
    }

    [Fact]
    public void WhenDriveRootThenReturnsDriveWithSeparator()
    {
        // On Windows, Path.GetFileName("C:") returns "" so GetDisplayName adds the separator back
        var result = PathHelper.GetDisplayName("C:" + Path.DirectorySeparatorChar);

        // Should return something like "C:\" on Windows
        Assert.False(string.IsNullOrEmpty(result));
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), result);
    }

    #endregion
}
