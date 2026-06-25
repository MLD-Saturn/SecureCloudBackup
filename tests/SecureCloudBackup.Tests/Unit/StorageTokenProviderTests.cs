using Azure.Core;
using SecureCloudBackup.Core.Services;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Pins the provider-neutral token-credential abstraction introduced when
/// <see cref="Azure.Core.TokenCredential"/> was removed from
/// <see cref="IObjectStorageService"/>: <see cref="AzureTokenCredentialProvider"/>
/// presents an Azure credential as the neutral <see cref="IStorageTokenProvider"/>,
/// and <see cref="AzureBlobService.ToTokenCredential"/> unwraps it directly (no
/// round-trip) while adapting any other neutral provider for the Azure SDK.
/// </summary>
public class StorageTokenProviderTests
{
    [Fact]
    public async Task AzureTokenCredentialProvider_GetTokenAsync_ReturnsWrappedCredentialToken()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var provider = new AzureTokenCredentialProvider(new StubTokenCredential(new AccessToken("token-abc", expiry)));

        var token = await provider.GetTokenAsync(["https://storage.azure.com/.default"]);

        Assert.Equal("token-abc", token.Token);
        Assert.Equal(expiry, token.ExpiresOn);
    }

    [Fact]
    public void AzureTokenCredentialProvider_NullCredential_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AzureTokenCredentialProvider(null!));
    }

    [Fact]
    public void ToTokenCredential_AzureProvider_UnwrapsOriginalCredentialWithoutAdapting()
    {
        var original = new StubTokenCredential(new AccessToken("t", DateTimeOffset.UtcNow.AddHours(1)));
        var provider = new AzureTokenCredentialProvider(original);

        var resolved = AzureBlobService.ToTokenCredential(provider);

        Assert.Same(original, resolved);
    }

    [Fact]
    public async Task ToTokenCredential_NonAzureProvider_AdaptsToAzureTokenCredential()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var resolved = AzureBlobService.ToTokenCredential(new StubTokenProvider("delegated", expiry));

        var azureToken = await resolved.GetTokenAsync(new TokenRequestContext(["scope"]), CancellationToken.None);

        Assert.Equal("delegated", azureToken.Token);
        Assert.Equal(expiry, azureToken.ExpiresOn);
    }

    [Fact]
    public async Task InMemoryBlobService_ConnectWithTokenAsync_NeutralProvider_SetsConnected()
    {
        using var encryption = new EncryptionService();
        var service = new InMemoryBlobService(encryption);

        await service.ConnectWithTokenAsync(
            new Uri("https://account.blob.core.windows.net"),
            "container",
            new StubTokenProvider("stub", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.True(service.IsConnected);
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        private readonly AccessToken _token;
        public StubTokenCredential(AccessToken token) => _token = token;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => _token;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(_token);
    }

    private sealed class StubTokenProvider : IStorageTokenProvider
    {
        private readonly string _token;
        private readonly DateTimeOffset _expiry;
        public StubTokenProvider(string token, DateTimeOffset expiry) => (_token, _expiry) = (token, expiry);

        public ValueTask<StorageAccessToken> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
            => new(new StorageAccessToken(_token, _expiry));
    }
}
