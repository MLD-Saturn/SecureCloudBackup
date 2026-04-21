using System;
using System.IO;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for the crash-safe atomic rename used by the legacy
/// unencrypted-to-encrypted and legacy-encrypted-to-Argon2id database
/// upgrade paths. The helper writes a sentinel file so a process crash
/// mid-rename can be completed on next launch.
/// </summary>
public class CommitDatabaseUpgradeTests : IDisposable
{
    private readonly string _tempDir;

    public CommitDatabaseUpgradeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AzbkCommitTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static void WriteText(string path, string content) => File.WriteAllText(path, content);

    [Fact]
    public void CommitDatabaseUpgrade_HappyPath_LeavesOnlyFinalArtifacts()
    {
        var db = Path.Combine(_tempDir, "backup.db");
        var temp = db + ".encrypted";
        WriteText(db, "OLD-PLAINTEXT-DB");
        WriteText(temp, "NEW-ENCRYPTED-DB");
        WriteText(temp + ".salt", "NEW-SALT");

        LocalDatabaseService.CommitDatabaseUpgrade(db, temp, ".unencrypted.bak");

        Assert.True(File.Exists(db), "Final database file must be in place");
        Assert.Equal("NEW-ENCRYPTED-DB", File.ReadAllText(db));
        Assert.True(File.Exists(db + ".salt"), "Salt file must be moved into place");
        Assert.Equal("NEW-SALT", File.ReadAllText(db + ".salt"));
        Assert.False(File.Exists(temp), "Temp file must be moved away");
        Assert.False(File.Exists(temp + ".salt"), "Temp salt must be moved away");
        Assert.False(File.Exists(db + ".unencrypted.bak"), "Backup must be deleted on success");
        Assert.False(File.Exists(db + ".upgrade-pending"), "Sentinel must be cleared on success");
    }

    [Fact]
    public void CommitDatabaseUpgrade_NoSaltFile_StillCommits()
    {
        // The legacy-encrypted upgrade may not produce a .salt sibling.
        var db = Path.Combine(_tempDir, "backup.db");
        var temp = db + ".upgraded";
        WriteText(db, "OLD-DB");
        WriteText(temp, "NEW-DB");

        LocalDatabaseService.CommitDatabaseUpgrade(db, temp, ".legacy.bak");

        Assert.True(File.Exists(db));
        Assert.Equal("NEW-DB", File.ReadAllText(db));
        Assert.False(File.Exists(temp));
        Assert.False(File.Exists(db + ".upgrade-pending"));
    }

    [Fact]
    public void CommitDatabaseUpgrade_MissingTemp_Throws()
    {
        var db = Path.Combine(_tempDir, "backup.db");
        WriteText(db, "OLD");
        Assert.Throws<FileNotFoundException>(() =>
            LocalDatabaseService.CommitDatabaseUpgrade(db, db + ".encrypted", ".bak"));
    }

    [Fact]
    public void RecoverInterruptedUpgrade_NoSentinel_IsNoop()
    {
        var db = Path.Combine(_tempDir, "backup.db");
        WriteText(db, "DB");
        var didWork = LocalDatabaseService.RecoverInterruptedUpgrade(db);
        Assert.False(didWork);
        Assert.Equal("DB", File.ReadAllText(db));
    }

    [Fact]
    public void RecoverInterruptedUpgrade_CrashBeforeStep1_FinishesSwap()
    {
        // Simulate: sentinel written, no rename happened yet. Both original
        // db and freshly migrated temp file are present.
        var db = Path.Combine(_tempDir, "backup.db");
        var temp = db + ".encrypted";
        WriteText(db, "ORIGINAL");
        WriteText(temp, "NEW-ENCRYPTED");
        WriteText(temp + ".salt", "NEW-SALT");
        WriteSentinel(db, temp, db + ".unencrypted.bak");

        var didWork = LocalDatabaseService.RecoverInterruptedUpgrade(db);

        Assert.True(didWork);
        Assert.Equal("NEW-ENCRYPTED", File.ReadAllText(db));
        Assert.Equal("NEW-SALT", File.ReadAllText(db + ".salt"));
        Assert.False(File.Exists(temp));
        Assert.False(File.Exists(db + ".unencrypted.bak"));
        Assert.False(File.Exists(db + ".upgrade-pending"));
    }

    [Fact]
    public void RecoverInterruptedUpgrade_CrashBetweenStep1AndStep2_FinishesSwap()
    {
        // Simulate: original was renamed to .bak, but temp -> final never ran.
        var db = Path.Combine(_tempDir, "backup.db");
        var temp = db + ".encrypted";
        var bak = db + ".unencrypted.bak";
        WriteText(bak, "ORIGINAL");      // already moved aside
        WriteText(temp, "NEW-ENCRYPTED");
        WriteText(temp + ".salt", "NEW-SALT");
        // No db file present at this moment - simulates the dangerous window.
        WriteSentinel(db, temp, bak);

        var didWork = LocalDatabaseService.RecoverInterruptedUpgrade(db);

        Assert.True(didWork);
        Assert.True(File.Exists(db), "Recovery must put the new file in place");
        Assert.Equal("NEW-ENCRYPTED", File.ReadAllText(db));
        Assert.Equal("NEW-SALT", File.ReadAllText(db + ".salt"));
        Assert.False(File.Exists(temp));
        Assert.False(File.Exists(bak), "Backup must be cleaned up after recovery");
        Assert.False(File.Exists(db + ".upgrade-pending"));
    }

    [Fact]
    public void RecoverInterruptedUpgrade_CrashBeforeBackupCleanup_DeletesBackup()
    {
        // Simulate: temp -> final completed, salt moved, but the .bak
        // delete didn't run. Sentinel still present.
        var db = Path.Combine(_tempDir, "backup.db");
        var temp = db + ".encrypted";
        var bak = db + ".unencrypted.bak";
        WriteText(db, "NEW-ENCRYPTED");
        WriteText(db + ".salt", "NEW-SALT");
        WriteText(bak, "ORIGINAL");
        WriteSentinel(db, temp, bak);

        var didWork = LocalDatabaseService.RecoverInterruptedUpgrade(db);

        Assert.True(didWork);
        Assert.Equal("NEW-ENCRYPTED", File.ReadAllText(db));
        Assert.False(File.Exists(bak));
        Assert.False(File.Exists(db + ".upgrade-pending"));
    }

    private static void WriteSentinel(string db, string temp, string bak)
    {
        // Mirrors the JSON shape CommitDatabaseUpgrade writes. Kept here as
        // a literal so a future shape change in the helper visibly breaks
        // these tests.
        var json = "{" +
                   $"\"databasePath\":\"{db.Replace("\\", "\\\\")}\"," +
                   $"\"tempPath\":\"{temp.Replace("\\", "\\\\")}\"," +
                   $"\"backupPath\":\"{bak.Replace("\\", "\\\\")}\"" +
                   "}";
        File.WriteAllText(db + ".upgrade-pending", json);
    }
}
