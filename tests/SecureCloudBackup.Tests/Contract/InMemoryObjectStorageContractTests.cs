using SecureCloudBackup.Core.Services;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Runs the <see cref="ObjectStorageContractTests"/> behavioral contract against
/// <see cref="InMemoryBlobService"/>. When a real cloud provider is added, create
/// a sibling subclass that connects that provider (against an emulator such as
/// Azurite / LocalStack / MinIO, or a live test account) and the entire contract
/// runs unchanged -- this is the acceptance gate every provider must pass.
/// </summary>
public sealed class InMemoryObjectStorageContractTests : ObjectStorageContractTests
{
    protected override async Task<IBlobStorageService> CreateConnectedServiceAsync(EncryptionService encryption)
    {
        var service = new InMemoryBlobService(encryption);
        await service.ConnectAsync("contract-connection-string", "contract-container");
        return service;
    }
}
