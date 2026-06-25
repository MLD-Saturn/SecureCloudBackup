using SecureCloudBackup.Core;
using SecureCloudBackup.Core.Models;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Unit tests for the operation-preview models in
/// <c>src/SecureCloudBackup.Core/Models/PreviewModels.cs</c>.
///
/// <para>
/// These types were at 0% coverage before this file. The computed
/// properties carry real safety-critical accounting: included-file
/// counts, transfer/delete byte totals, the case-insensitive
/// excluded-path set, and the destructive-action warning text the UI
/// shows before a delete or overwrite. A silent bug here would
/// mis-state what an irreversible operation is about to do, so the
/// behavior is pinned explicitly rather than left to manual UI checks.
/// </para>
/// </summary>
public class PreviewModelsTests
{
    private static PreviewFileAction File(string path, long size, bool included = true) => new()
    {
        FilePath = path,
        FileSize = size,
        IsIncluded = included
    };

    #region PreviewFileAction

    [Fact]
    public void FileNameReturnsLeafOfPath()
    {
        var action = File(Path.Combine("C:", "data", "report.pdf"), 0);

        Assert.Equal("report.pdf", action.FileName);
    }

    [Fact]
    public void DirectoryReturnsContainingFolder()
    {
        var dir = Path.Combine("C:", "data", "sub");
        var action = File(Path.Combine(dir, "report.pdf"), 0);

        Assert.Equal(dir, action.Directory);
    }

    [Fact]
    public void FileSizeTextMatchesFormatHelper()
    {
        var action = File("a.bin", 1536);

        Assert.Equal(FormatHelper.FormatBytes(1536), action.FileSizeText);
    }

    [Fact]
    public void LastModifiedTextIsEmptyWhenDefault()
    {
        var action = File("a.bin", 0);

        Assert.Equal(string.Empty, action.LastModifiedText);
    }

    [Fact]
    public void LastModifiedTextIsFormattedWhenSet()
    {
        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var action = new PreviewFileAction { FilePath = "a.bin", LastModified = when };

        Assert.Equal(when.ToString("g"), action.LastModifiedText);
    }

    [Fact]
    public void IsIncludedRaisesPropertyChangedWhenValueChanges()
    {
        var action = File("a.bin", 0, included: true);
        var raised = false;
        action.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewFileAction.IsIncluded))
                raised = true;
        };

        action.IsIncluded = false;

        Assert.True(raised);
    }

    [Fact]
    public void IsIncludedDoesNotRaisePropertyChangedWhenValueUnchanged()
    {
        var action = File("a.bin", 0, included: true);
        var raised = false;
        action.PropertyChanged += (_, _) => raised = true;

        action.IsIncluded = true;

        Assert.False(raised);
    }

    #endregion

    #region Counts

    [Fact]
    public void CountPropertiesReflectListSizes()
    {
        var preview = new OperationPreview
        {
            FilesToCreate = [File("a", 1), File("b", 2)],
            FilesToOverwrite = [File("c", 3)],
            FilesToDelete = [File("d", 4), File("e", 5), File("f", 6)],
            FilesToSkip = [File("g", 7)]
        };

        Assert.Equal(2, preview.CreateCount);
        Assert.Equal(1, preview.OverwriteCount);
        Assert.Equal(3, preview.DeleteCount);
        Assert.Equal(1, preview.SkipCount);
    }

    [Fact]
    public void IncludedCountsExcludeUncheckedFiles()
    {
        var preview = new OperationPreview
        {
            FilesToCreate = [File("a", 1), File("b", 2, included: false)],
            FilesToOverwrite = [File("c", 3, included: false)],
            FilesToDelete = [File("d", 4), File("e", 5)]
        };

        Assert.Equal(1, preview.IncludedCreateCount);
        Assert.Equal(0, preview.IncludedOverwriteCount);
        Assert.Equal(2, preview.IncludedDeleteCount);
    }

    #endregion

    #region Byte totals

    [Fact]
    public void TotalBytesToTransferSumsIncludedCreatesAndOverwrites()
    {
        var preview = new OperationPreview
        {
            FilesToCreate = [File("a", 100), File("b", 200, included: false)],
            FilesToOverwrite = [File("c", 50)]
        };

        Assert.Equal(150, preview.TotalBytesToTransfer);
    }

    [Fact]
    public void TotalBytesToDeleteSumsIncludedDeletesOnly()
    {
        var preview = new OperationPreview
        {
            FilesToDelete = [File("a", 100), File("b", 200, included: false), File("c", 300)]
        };

        Assert.Equal(400, preview.TotalBytesToDelete);
    }

    #endregion

    #region ExcludedFilePaths

    [Fact]
    public void ExcludedFilePathsContainsOnlyUncheckedFiles()
    {
        var preview = new OperationPreview
        {
            FilesToCreate = [File("keep1", 1), File("drop1", 1, included: false)],
            FilesToOverwrite = [File("drop2", 1, included: false)],
            FilesToDelete = [File("keep2", 1)]
        };

        Assert.Equal(["drop1", "drop2"], preview.ExcludedFilePaths.OrderBy(p => p));
    }

    [Fact]
    public void ExcludedFilePathsIsCaseInsensitive()
    {
        var preview = new OperationPreview
        {
            FilesToCreate = [File(@"C:\Data\File.txt", 1, included: false)]
        };

        Assert.Contains(@"c:\data\file.txt", preview.ExcludedFilePaths);
    }

    #endregion

    #region Destructive / change flags

    [Fact]
    public void HasDestructiveActionsTrueWhenDeletesPresent()
    {
        var preview = new OperationPreview { FilesToDelete = [File("a", 1)] };

        Assert.True(preview.HasDestructiveActions);
    }

    [Fact]
    public void HasDestructiveActionsTrueWhenOverwritesPresent()
    {
        var preview = new OperationPreview { FilesToOverwrite = [File("a", 1)] };

        Assert.True(preview.HasDestructiveActions);
    }

    [Fact]
    public void HasDestructiveActionsFalseForCreateOnly()
    {
        var preview = new OperationPreview { FilesToCreate = [File("a", 1)] };

        Assert.False(preview.HasDestructiveActions);
    }

    [Fact]
    public void HasChangesFalseWhenOnlySkips()
    {
        var preview = new OperationPreview { FilesToSkip = [File("a", 1)] };

        Assert.False(preview.HasChanges);
    }

    [Fact]
    public void HasChangesTrueWhenCreatesPresent()
    {
        var preview = new OperationPreview { FilesToCreate = [File("a", 1)] };

        Assert.True(preview.HasChanges);
    }

    #endregion

    #region EffectiveStorageTier

    [Fact]
    public void EffectiveStorageTierUsesDefaultWhenNoSelection()
    {
        var preview = new OperationPreview { DefaultStorageTier = StorageTier.Cool };

        Assert.Equal(StorageTier.Cool, preview.EffectiveStorageTier);
    }

    [Fact]
    public void EffectiveStorageTierPrefersSelectionOverDefault()
    {
        var preview = new OperationPreview
        {
            DefaultStorageTier = StorageTier.Cool,
            SelectedStorageTier = StorageTier.Archive
        };

        Assert.Equal(StorageTier.Archive, preview.EffectiveStorageTier);
    }

    #endregion

    #region WarningMessage

    [Fact]
    public void WarningMessageForDeleteFromAzureMentionsPermanentDeletion()
    {
        var preview = new OperationPreview
        {
            OperationType = OperationType.DeleteFromAzure,
            FilesToDelete = [File("a", 1)]
        };

        Assert.Contains("PERMANENTLY", preview.WarningMessage);
    }

    [Fact]
    public void WarningMessageForMirrorSyncMentionsLocalDeletion()
    {
        var preview = new OperationPreview
        {
            OperationType = OperationType.MirrorSync,
            FilesToDelete = [File("a", 1)]
        };

        Assert.Contains("local file", preview.WarningMessage);
    }

    [Fact]
    public void WarningMessageForOverwriteMentionsOverwrite()
    {
        var preview = new OperationPreview
        {
            OperationType = OperationType.Restore,
            FilesToOverwrite = [File("a", 1)]
        };

        Assert.Contains("overwritten", preview.WarningMessage);
    }

    [Fact]
    public void WarningMessageForBackupMentionsUpload()
    {
        var preview = new OperationPreview
        {
            OperationType = OperationType.Backup,
            FilesToCreate = [File("a", 1)]
        };

        Assert.Contains("uploaded", preview.WarningMessage);
    }

    [Fact]
    public void WarningMessageIsNullWhenNoActionableFiles()
    {
        var preview = new OperationPreview
        {
            OperationType = OperationType.Backup,
            FilesToSkip = [File("a", 1)]
        };

        Assert.Null(preview.WarningMessage);
    }

    #endregion
}
