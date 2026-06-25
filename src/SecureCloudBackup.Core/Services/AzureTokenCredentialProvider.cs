using Azure.Core;

namespace SecureCloudBackup.Core.Services;

/// <summary>
/// Azure adapter that presents an <see cref="Azure.Core.TokenCredential"/> as the
/// provider-neutral <see cref="IStorageTokenProvider"/>. This is the bridge that
/// lets the Azure interactive sign-in flow (an Azure.Identity
/// <see cref="TokenCredential"/>) pass through the neutral
/// <see cref="IObjectStorageService"/> contract without the interface naming any
/// Azure type. <see cref="AzureBlobService"/> unwraps the original credential
/// directly when it recognises this type, so no token round-trips through the
/// neutral surface on the Azure path.
/// <para>
/// Lives in the Core assembly for now; it moves to the dedicated Azure provider
/// project when the Azure SDK is extracted from Core (W-provider-abstraction
/// Phase 8).
/// </para>
/// </summary>
internal sealed class AzureTokenCredentialProvider : IStorageTokenProvider
{
    /// <summary>The wrapped Azure credential, consumed directly by <see cref="AzureBlobService"/>.</summary>
    public TokenCredential Credential { get; }

    public AzureTokenCredentialProvider(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        Credential = credential;
    }

    public async ValueTask<StorageAccessToken> GetTokenAsync(
        IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var token = await Credential.GetTokenAsync(
            new TokenRequestContext([.. scopes]), cancellationToken).ConfigureAwait(false);
        return new StorageAccessToken(token.Token, token.ExpiresOn);
    }
}
