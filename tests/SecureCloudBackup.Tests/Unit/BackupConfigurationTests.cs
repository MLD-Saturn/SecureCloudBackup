using SecureCloudBackup.Core.Models;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Unit tests for the computed properties on
/// <see cref="BackupConfiguration"/> in
/// <c>src/SecureCloudBackup.Core/Models/ConfigurationModels.cs</c>.
///
/// <para>
/// These properties were uncovered before this file. They drive
/// connection setup (<see cref="BackupConfiguration.BlobServiceUri"/>)
/// and the "is Azure ready?" gate
/// (<see cref="BackupConfiguration.IsStorageConfigured"/>) that decides
/// whether backup/restore can run at all, so each branch is pinned.
/// </para>
/// </summary>
public class BackupConfigurationTests
{
    #region BlobServiceUri

    [Fact]
    public void BlobServiceUriIsNullWhenAccountNameMissing()
    {
        var config = new BackupConfiguration { StorageAccountName = null };

        Assert.Null(config.BlobServiceUri);
    }

    [Fact]
    public void BlobServiceUriIsNullWhenAccountNameEmpty()
    {
        var config = new BackupConfiguration { StorageAccountName = string.Empty };

        Assert.Null(config.BlobServiceUri);
    }

    [Fact]
    public void BlobServiceUriIsBuiltFromAccountName()
    {
        var config = new BackupConfiguration { StorageAccountName = "mystorageaccount" };

        Assert.Equal(
            new Uri("https://mystorageaccount.blob.core.windows.net"),
            config.BlobServiceUri);
    }

    #endregion

    #region IsStorageConfigured

    [Fact]
    public void IsStorageConfiguredFalseForConnectionStringWhenNoEncryptedString()
    {
        var config = new BackupConfiguration
        {
            AuthMethod = StorageAuthMethod.ConnectionString,
            EncryptedConnectionString = null
        };

        Assert.False(config.IsStorageConfigured);
    }

    [Fact]
    public void IsStorageConfiguredTrueForConnectionStringWhenEncryptedStringPresent()
    {
        var config = new BackupConfiguration
        {
            AuthMethod = StorageAuthMethod.ConnectionString,
            EncryptedConnectionString = [1, 2, 3]
        };

        Assert.True(config.IsStorageConfigured);
    }

    [Fact]
    public void IsStorageConfiguredFalseForEntraIdWhenNotAuthenticated()
    {
        var config = new BackupConfiguration
        {
            AuthMethod = StorageAuthMethod.EntraId,
            IsEntraIdAuthenticated = false,
            StorageAccountName = "acct"
        };

        Assert.False(config.IsStorageConfigured);
    }

    [Fact]
    public void IsStorageConfiguredFalseForEntraIdWhenAccountNameMissing()
    {
        var config = new BackupConfiguration
        {
            AuthMethod = StorageAuthMethod.EntraId,
            IsEntraIdAuthenticated = true,
            StorageAccountName = null
        };

        Assert.False(config.IsStorageConfigured);
    }

    [Fact]
    public void IsStorageConfiguredTrueForEntraIdWhenAuthenticatedWithAccountName()
    {
        var config = new BackupConfiguration
        {
            AuthMethod = StorageAuthMethod.EntraId,
            IsEntraIdAuthenticated = true,
            StorageAccountName = "acct"
        };

        Assert.True(config.IsStorageConfigured);
    }

    #endregion

    #region Provider discriminator

    [Fact]
    public void ProviderDefaultsToAzureBlob()
    {
        var config = new BackupConfiguration();

        Assert.Equal(StorageProvider.AzureBlob, config.Provider);
    }

    #endregion

    #region LockoutUntilUtc

    [Fact]
    public void LockoutUntilUtcIsNullWhenTicksUnset()
    {
        var config = new BackupConfiguration { LockoutUntilTicks = null };

        Assert.Null(config.LockoutUntilUtc);
    }

    [Fact]
    public void LockoutUntilUtcRoundTripsThroughTicksAsUtc()
    {
        var when = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var config = new BackupConfiguration { LockoutUntilUtc = when };

        Assert.Equal(when.Ticks, config.LockoutUntilTicks);
        Assert.Equal(DateTimeKind.Utc, config.LockoutUntilUtc!.Value.Kind);
        Assert.Equal(when, config.LockoutUntilUtc!.Value);
    }

    #endregion
}
