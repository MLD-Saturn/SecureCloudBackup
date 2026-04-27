using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1c: round-trip tests for the SQLite backend's
/// <see cref="BackupConfiguration"/> persistence. Covers every field on
/// the model plus the nested watched-folder + global-exclude lists.
///
/// <para>
/// Mirrors the assertions in <c>LocalDatabaseServiceTests</c> for
/// configuration so that a future commit can run both backends through
/// the same logical assertions to confirm equivalence.
/// </para>
/// </summary>
public class SqliteBackendConfigurationTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly SqliteBackend _backend;

    public SqliteBackendConfigurationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "cfg.db");
        _backend = new SqliteBackend();
        _backend.Initialize(_dbPath, "CfgTestPwd!".AsSpan());
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void GetConfiguration_FreshDatabase_ReturnsDefaults()
    {
        // Arrange + Act: a fresh DB has the seeded singleton row with default
        // column values; we expect it to read back as `new BackupConfiguration()`.
        var config = _backend.GetConfiguration();
        var defaults = new BackupConfiguration();

        // Assert: every field matches the C# model defaults.
        Assert.Equal(defaults.AuthMethod, config.AuthMethod);
        Assert.Equal(defaults.StorageAccountName, config.StorageAccountName);
        Assert.Equal(defaults.EncryptedConnectionString, config.EncryptedConnectionString);
        Assert.Equal(defaults.ContainerName, config.ContainerName);
        Assert.Equal(defaults.PasswordSalt, config.PasswordSalt);
        Assert.Equal(defaults.PasswordVerificationHash, config.PasswordVerificationHash);
        Assert.Equal(defaults.LastBackupTime, config.LastBackupTime);
        Assert.Equal(defaults.TotalBytesUploaded, config.TotalBytesUploaded);
        Assert.Equal(defaults.FailedLoginAttempts, config.FailedLoginAttempts);
        Assert.Equal(defaults.LockoutUntilTicks, config.LockoutUntilTicks);
        Assert.Equal(defaults.IsEntraIdAuthenticated, config.IsEntraIdAuthenticated);
        Assert.Equal(defaults.EntraIdUserName, config.EntraIdUserName);
        Assert.Equal(defaults.ConfigVersion, config.ConfigVersion);
        Assert.Equal(defaults.MemoryLimitEnabled, config.MemoryLimitEnabled);
        // B29: MemoryLimitMB on a fresh DB is the hardware-aware
        // recommended default (NULL column -> SystemMemoryHelper
        // computes 25% of total physical RAM, snapped down to a
        // slider step, capped at 8 GB). The C# model default is the
        // 8 GB cap, so on most CI/dev hosts these will match; on a
        // 4 GB or 8 GB host they intentionally do NOT match (the
        // recommended default is smaller). Assert against the helper,
        // not the model default, so the test is correct on every
        // hardware tier.
        Assert.Equal(SystemMemoryHelper.GetRecommendedDefaultLimitMB(), config.MemoryLimitMB);
        Assert.Empty(config.WatchedFolders);
        Assert.Empty(config.GlobalExcludePatterns);
    }

    [Fact]
    public void SaveConfiguration_RoundTripsAllScalarFields()
    {
        // Arrange: every nullable / non-default field set to a distinctive value.
        var fixedTime = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
        var saved = new BackupConfiguration
        {
            AuthMethod = AzureAuthMethod.EntraId,
            StorageAccountName = "mystorageaccount",
            EncryptedConnectionString = [0x01, 0x02, 0x03, 0x04],
            ContainerName = "custom-container",
            PasswordSalt = [0x10, 0x20, 0x30],
            PasswordVerificationHash = [0xAA, 0xBB, 0xCC, 0xDD],
            LastBackupTime = fixedTime,
            TotalBytesUploaded = 9876543210,
            FailedLoginAttempts = 2,
            LockoutUntilTicks = fixedTime.AddMinutes(15).Ticks,
            IsEntraIdAuthenticated = true,
            EntraIdUserName = "user@contoso.com",
            ConfigVersion = 4,
            MemoryLimitEnabled = true,
            MemoryLimitMB = 8192,
        };

        // Act
        _backend.SaveConfiguration(saved);
        var loaded = _backend.GetConfiguration();

        // Assert
        Assert.Equal(saved.AuthMethod, loaded.AuthMethod);
        Assert.Equal(saved.StorageAccountName, loaded.StorageAccountName);
        Assert.Equal(saved.EncryptedConnectionString, loaded.EncryptedConnectionString);
        Assert.Equal(saved.ContainerName, loaded.ContainerName);
        Assert.Equal(saved.PasswordSalt, loaded.PasswordSalt);
        Assert.Equal(saved.PasswordVerificationHash, loaded.PasswordVerificationHash);
        Assert.Equal(saved.LastBackupTime, loaded.LastBackupTime);
        Assert.Equal(saved.TotalBytesUploaded, loaded.TotalBytesUploaded);
        Assert.Equal(saved.FailedLoginAttempts, loaded.FailedLoginAttempts);
        Assert.Equal(saved.LockoutUntilTicks, loaded.LockoutUntilTicks);
        Assert.Equal(saved.IsEntraIdAuthenticated, loaded.IsEntraIdAuthenticated);
        Assert.Equal(saved.EntraIdUserName, loaded.EntraIdUserName);
        Assert.Equal(saved.ConfigVersion, loaded.ConfigVersion);
        Assert.Equal(saved.MemoryLimitEnabled, loaded.MemoryLimitEnabled);
        Assert.Equal(saved.MemoryLimitMB, loaded.MemoryLimitMB);
    }

    [Fact]
    public void SaveConfiguration_RoundTripsWatchedFoldersInOrder()
    {
        // Arrange
        var saved = new BackupConfiguration
        {
            WatchedFolders =
            {
                new WatchedFolder
                {
                    Path = @"C:\Photos",
                    StorageTier = StorageTier.Cool,
                    IsEnabled = true,
                    ExcludePatterns = { "*.tmp", "Thumbs.db" },
                    ExcludeSubfolders = { "Cache" },
                },
                new WatchedFolder
                {
                    Path = @"D:\Documents",
                    StorageTier = StorageTier.Archive,
                    IsEnabled = false,
                    // Only ExcludeSubfolders, no ExcludePatterns - exercises
                    // the empty-list branch of InsertExcludes.
                    ExcludeSubfolders = { "Drafts", "Old" },
                },
            },
        };

        // Act
        _backend.SaveConfiguration(saved);
        var loaded = _backend.GetConfiguration();

        // Assert: order preserved, every field round-trips.
        Assert.Equal(2, loaded.WatchedFolders.Count);

        var first = loaded.WatchedFolders[0];
        Assert.Equal(@"C:\Photos", first.Path);
        Assert.Equal(StorageTier.Cool, first.StorageTier);
        Assert.True(first.IsEnabled);
        Assert.Equal(new[] { "*.tmp", "Thumbs.db" }, first.ExcludePatterns);
        Assert.Equal(new[] { "Cache" }, first.ExcludeSubfolders);

        var second = loaded.WatchedFolders[1];
        Assert.Equal(@"D:\Documents", second.Path);
        Assert.Equal(StorageTier.Archive, second.StorageTier);
        Assert.False(second.IsEnabled);
        Assert.Empty(second.ExcludePatterns);
        Assert.Equal(new[] { "Drafts", "Old" }, second.ExcludeSubfolders);
    }

    [Fact]
    public void SaveConfiguration_RoundTripsGlobalExcludePatterns()
    {
        var saved = new BackupConfiguration
        {
            GlobalExcludePatterns = { "node_modules", ".git", "*.log" },
        };

        _backend.SaveConfiguration(saved);
        var loaded = _backend.GetConfiguration();

        Assert.Equal(new[] { "node_modules", ".git", "*.log" }, loaded.GlobalExcludePatterns);
    }

    [Fact]
    public void SaveConfiguration_OverwritesPreviousFolderList()
    {
        // Arrange: save with two folders, then overwrite with one different folder.
        _backend.SaveConfiguration(new BackupConfiguration
        {
            WatchedFolders =
            {
                new WatchedFolder { Path = @"C:\First" },
                new WatchedFolder { Path = @"C:\Second" },
            },
        });

        _backend.SaveConfiguration(new BackupConfiguration
        {
            WatchedFolders = { new WatchedFolder { Path = @"C:\OnlyThisOne" } },
        });

        var loaded = _backend.GetConfiguration();

        // Assert: the previous two folders are gone; only the most recent set remains.
        Assert.Single(loaded.WatchedFolders);
        Assert.Equal(@"C:\OnlyThisOne", loaded.WatchedFolders[0].Path);
    }

    [Fact]
    public void SaveConfiguration_SurvivesReopen()
    {
        // Arrange
        var saved = new BackupConfiguration
        {
            ContainerName = "persistent-container",
            TotalBytesUploaded = 12345,
            WatchedFolders = { new WatchedFolder { Path = @"C:\Persist" } },
            GlobalExcludePatterns = { "*.bak" },
        };
        _backend.SaveConfiguration(saved);
        _backend.Dispose();

        // Act: reopen.
        using var reopened = new SqliteBackend();
        reopened.Initialize(_dbPath, "CfgTestPwd!".AsSpan());
        var loaded = reopened.GetConfiguration();

        // Assert
        Assert.Equal("persistent-container", loaded.ContainerName);
        Assert.Equal(12345, loaded.TotalBytesUploaded);
        Assert.Single(loaded.WatchedFolders);
        Assert.Equal(@"C:\Persist", loaded.WatchedFolders[0].Path);
        Assert.Equal(new[] { "*.bak" }, loaded.GlobalExcludePatterns);
    }

    [Fact]
    public void SaveConfiguration_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _backend.SaveConfiguration(null!));
    }
}
