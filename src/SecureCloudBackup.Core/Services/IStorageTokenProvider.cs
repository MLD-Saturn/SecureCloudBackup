namespace SecureCloudBackup.Core.Services;

/// <summary>
/// A bearer access token plus its expiry, returned by an
/// <see cref="IStorageTokenProvider"/>. The provider-neutral equivalent of a
/// cloud SDK's access-token struct, with no SDK dependency.
/// </summary>
/// <param name="Token">The bearer token string.</param>
/// <param name="ExpiresOn">When the token expires.</param>
public readonly record struct StorageAccessToken(string Token, DateTimeOffset ExpiresOn);

/// <summary>
/// Provider-neutral source of bearer access tokens for a token-authenticated
/// storage connection -- the abstraction the object-storage interface uses in
/// place of any cloud SDK's credential type (e.g. Azure's <c>TokenCredential</c>).
/// A provider adapter consumes this to authenticate without Core knowing how the
/// tokens are obtained (interactive sign-in, managed identity, workload identity,
/// etc.).
/// <para>
/// This covers the interactive / federated token shape; the static-secret
/// connection shape is handled separately by
/// <see cref="IBlobStorageService.ConnectAsync(string, string)"/>.
/// </para>
/// </summary>
public interface IStorageTokenProvider
{
    /// <summary>
    /// Acquires an access token for the requested OAuth <paramref name="scopes"/>.
    /// </summary>
    /// <param name="scopes">The OAuth scopes the token must cover.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<StorageAccessToken> GetTokenAsync(
        IReadOnlyList<string> scopes, CancellationToken cancellationToken = default);
}
