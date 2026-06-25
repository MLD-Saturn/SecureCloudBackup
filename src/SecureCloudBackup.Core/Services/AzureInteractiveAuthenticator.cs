using Azure.Identity;

namespace SecureCloudBackup.Core.Services;

/// <summary>
/// Azure implementation of <see cref="IStorageAuthenticator"/>: runs an
/// interactive Microsoft Entra ID browser sign-in via Azure.Identity's
/// <see cref="InteractiveBrowserCredential"/> and returns the credential as a
/// provider-neutral <see cref="IStorageTokenProvider"/>
/// (an <see cref="AzureTokenCredentialProvider"/>). This is where the
/// Azure.Identity interactive flow lives now that <see cref="BackupOrchestrator"/>
/// is provider-neutral; it moves to the dedicated Azure project in
/// W-provider-abstraction Phase 8.
/// </summary>
public sealed class AzureInteractiveAuthenticator : IStorageAuthenticator
{
    // Azure Storage data-plane OAuth scope.
    private static readonly string[] StorageScopes = ["https://storage.azure.com/.default"];

    public async Task<StorageAuthenticationResult> AuthenticateInteractiveAsync(
        CancellationToken cancellationToken, int timeoutSeconds)
    {
        try
        {
            InteractiveBrowserCredentialOptions options = new()
            {
                // Use the system's default browser.
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = "SecureCloudBackup",
                    UnsafeAllowUnencryptedStorage = false
                },
                // Redirect to localhost after auth completes.
                RedirectUri = new Uri("http://localhost")
            };

            var credential = new InteractiveBrowserCredential(options);

            // Bound the interactive flow by the supplied timeout while still
            // honouring a user cancellation.
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            // Force a token request to trigger the browser login.
            var token = await credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(StorageScopes), linkedCts.Token);

            if (string.IsNullOrEmpty(token.Token))
                return new StorageAuthenticationResult(false, "Authentication did not return a valid token.", null);

            return new StorageAuthenticationResult(
                true, "Successfully authenticated with Microsoft Entra ID!", new AzureTokenCredentialProvider(credential));
        }
        catch (AuthenticationFailedException ex)
        {
            // Provide more user-friendly messages for common errors.
            if (ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return new StorageAuthenticationResult(false, "Sign-in was cancelled. Please try again.", null);
            }
            if (ex.Message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase))
            {
                return new StorageAuthenticationResult(false, $"Microsoft sign-in error: {ex.Message}", null);
            }
            return new StorageAuthenticationResult(false, $"Authentication failed: {ex.Message}", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageAuthenticationResult(false, "Sign-in was cancelled.", null);
        }
        catch (OperationCanceledException)
        {
            // Timeout (the linked token fired from the timeout source, not the caller).
            return new StorageAuthenticationResult(
                false, $"Sign-in timed out after {timeoutSeconds} seconds. Please try again.", null);
        }
        catch (Exception ex)
        {
            // Handle browser-related errors.
            if (ex.Message.Contains("browser", StringComparison.OrdinalIgnoreCase))
            {
                return new StorageAuthenticationResult(
                    false, "Could not open browser for sign-in. Please ensure a browser is available.", null);
            }
            return new StorageAuthenticationResult(false, $"Authentication error: {ex.Message}", null);
        }
    }
}
