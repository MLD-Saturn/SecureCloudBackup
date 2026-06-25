using System;
using SecureCloudBackup.Core.Models;
using SecureCloudBackup.Core.Services;

namespace SecureCloudBackup.ViewModels;

/// <summary>
/// Composition-root factory that selects the object-storage provider (and its
/// interactive authenticator) for a given <see cref="StorageProvider"/>. This is
/// the single place the app maps the provider-neutral
/// <see cref="IObjectStorageService"/> / <see cref="IStorageAuthenticator"/>
/// contracts onto a concrete provider, so adding a new cloud is a new
/// <c>switch</c> arm here plus a new provider project -- nothing in
/// <c>SecureCloudBackup.Core</c> changes.
/// </summary>
internal static class ObjectStorageProviderFactory
{
    /// <summary>
    /// Creates the storage service + interactive authenticator for
    /// <paramref name="provider"/>.
    /// </summary>
    public static (IObjectStorageService Service, IStorageAuthenticator Authenticator) Create(
        StorageProvider provider, EncryptionService encryptionService)
    {
        ArgumentNullException.ThrowIfNull(encryptionService);

        return provider switch
        {
            StorageProvider.AzureBlob => (
                new AzureBlobService(encryptionService),
                new AzureInteractiveAuthenticator()),
            _ => throw new NotSupportedException($"Unsupported storage provider: {provider}")
        };
    }
}
