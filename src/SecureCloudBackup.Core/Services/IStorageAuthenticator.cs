namespace SecureCloudBackup.Core.Services;

/// <summary>
/// Result of an interactive storage sign-in: a success flag, a user-facing
/// message, and (on success) the provider-neutral token source to use for
/// subsequent connections.
/// </summary>
/// <param name="Success">Whether the sign-in succeeded.</param>
/// <param name="Message">A user-facing status/error message.</param>
/// <param name="TokenProvider">The token source on success; <see langword="null"/> on failure.</param>
public sealed record StorageAuthenticationResult(bool Success, string Message, IStorageTokenProvider? TokenProvider);

/// <summary>
/// Provider-neutral interactive authenticator. A provider adapter performs its
/// own sign-in (e.g. an Azure Entra ID browser flow) and returns a neutral
/// <see cref="IStorageTokenProvider"/> the orchestrator can hand to
/// <see cref="IBlobStorageService.ConnectWithTokenAsync"/> -- without the
/// orchestrator knowing how the sign-in was performed. This is the seam that
/// keeps the interactive auth flow (and its cloud SDK dependency) out of
/// <see cref="BackupOrchestrator"/>.
/// </summary>
public interface IStorageAuthenticator
{
    /// <summary>
    /// Performs an interactive sign-in, returning a token provider on success.
    /// Implementations should not throw for an expected sign-in failure
    /// (cancellation, timeout, bad credentials); they return a failed
    /// <see cref="StorageAuthenticationResult"/> with a user-facing message.
    /// </summary>
    /// <param name="cancellationToken">Cancels the sign-in (e.g. user cancel).</param>
    /// <param name="timeoutSeconds">Maximum time to wait for the interactive flow.</param>
    Task<StorageAuthenticationResult> AuthenticateInteractiveAsync(
        CancellationToken cancellationToken, int timeoutSeconds);
}
