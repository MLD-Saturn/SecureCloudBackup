using AzureBackup.Core.Models;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for the computed properties on
/// <see cref="BackupConfiguration"/> in
/// <c>src/AzureBackup.Core/Models/ConfigurationModels.cs</c>.
///
/// <para>
/// These properties were uncovered before this file. They drive
/// connection setup (<see cref="BackupConfiguration.BlobServiceUri"/>)
/// and the "is Azure ready?" gate
/// (<see cref="BackupConfiguration.IsAzureConfigured"/>) that decides
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

    #region IsAzureConfigured

    [Fact]
    public void IsAzureConfiguredFalseForConnectionStringWhenNoEncryptedString()
    {
        var config = new BackupConfiguration
        {
            AuthMethod = AzureAuthMethod.ConnectionString,
            EncryptedConnectionString = null
        };

        Assert.False(config.IsAzureConfigured);
    }

    [Fact]
    public void IsAzureConfiguredTrueForConnectionStringWhenEncryptedStringPresent()
    {
        var config = new BackupConfiguration
        {
            AuthMethod = AzureAuthMethod.ConnectionString,
            EncryptedConnectionString = [1, 2, 3]
        };

        Assert.True(config.IsAzureConfigured);
    }

    [Fact]
    public void IsAzureConfiguredFalseForEntraIdWhenNotAuthenticated()
    {
        var config = new BackupConfiguration
        {
            AuthMethod = AzureAuthMethod.EntraId,
            IsEntraIdAuthenticated = false,
            StorageAccountName = "acct"
        };

        Assert.False(config.IsAzureConfigured);
    }

    [Fact]
    public void IsAzureConfiguredFalseForEntraIdWhenAccountNameMissing()
    {
        var config = new BackupConfiguration
        {
            AuthMethod = AzureAuthMethod.EntraId,
            IsEntraIdAuthenticated = true,
            StorageAccountName = null
        };

        Assert.False(config.IsAzureConfigured);
    }

    [Fact]
    public void IsAzureConfiguredTrueForEntraIdWhenAuthenticatedWithAccountName()
    {
        var config = new BackupConfiguration
        {
            AuthMethod = AzureAuthMethod.EntraId,
            IsEntraIdAuthenticated = true,
            StorageAccountName = "acct"
        };

        Assert.True(config.IsAzureConfigured);
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
