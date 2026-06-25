using SecureCloudBackup.Core.Models;

namespace SecureCloudBackup.Core.Services;

/// <summary>
/// Authentication methods: interactive (token) sign-in and connection string.
/// </summary>
public partial class BackupOrchestrator
{
    /// <summary>
    /// Authenticates with an interactive sign-in (e.g. Microsoft Entra ID browser flow)
    /// via the provider-supplied <see cref="IStorageAuthenticator"/> and caches the
    /// resulting provider-neutral token source. Use this for organizational/work accounts only.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <param name="timeoutSeconds">Timeout in seconds for the authentication (default: 120 seconds)</param>
    public async Task<(bool success, string message)> AuthenticateWithEntraIdAsync(
        CancellationToken cancellationToken = default,
        int timeoutSeconds = 120)
    {
        Log($"AuthenticateWithEntraIdAsync: Starting interactive authentication (timeout={timeoutSeconds}s)");

        if (_authenticator == null)
        {
            Log("AuthenticateWithEntraIdAsync: No interactive authenticator configured");
            return (false, "Interactive sign-in is not available in this configuration.");
        }

        var result = await _authenticator.AuthenticateInteractiveAsync(cancellationToken, timeoutSeconds);

        if (result.Success && result.TokenProvider != null)
        {
            _tokenProvider = result.TokenProvider;
            Log("AuthenticateWithEntraIdAsync: Token obtained successfully");

            // Update config to mark as authenticated.
            var config = _databaseService.GetConfiguration();
            config.IsEntraIdAuthenticated = true;
            _databaseService.SaveConfiguration(config);

            return (true, result.Message);
        }

        _tokenProvider = null;
        Log($"AuthenticateWithEntraIdAsync: Sign-in failed - {result.Message}");
        return (false, result.Message);
    }

    /// <summary>
    /// Tests the connection to Azure storage using the current Entra ID credential.
    /// </summary>
    public async Task<(bool success, string message)> TestRemoteConnectionAsync(
        string storageAccountName, string bucketName)
    {
        Log($"TestRemoteConnectionAsync: Testing Entra ID connection to {storageAccountName}/{bucketName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(storageAccountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        
        if (_tokenProvider == null)
        {
            Log("TestRemoteConnectionAsync: No credential available");
            return (false, "Not authenticated with Entra ID. Please sign in first.");
        }

        Uri blobServiceUri = new($"https://{storageAccountName}.blob.core.windows.net");
        var result = await _blobService.TestConnectionWithTokenAsync(blobServiceUri, bucketName, _tokenProvider);
        Log($"TestRemoteConnectionAsync: Result success={result.success}");
        return result;
    }
    
    /// <summary>
    /// Saves the Azure storage account configuration (uses Entra ID, no connection string needed).
    /// </summary>
    public async Task SaveStorageAccountAsync(string storageAccountName, string bucketName)
    {
        Log($"SaveStorageAccountAsync: Saving Entra ID config for {storageAccountName}/{bucketName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(storageAccountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        
        if (_tokenProvider == null)
            throw new InvalidOperationException("Must authenticate with Entra ID first.");
        
        var config = _databaseService.GetConfiguration();
        config.StorageAccountName = storageAccountName;
        config.ContainerName = bucketName;
        config.IsEntraIdAuthenticated = true;
        config.AuthMethod = StorageAuthMethod.EntraId;
        _databaseService.SaveConfiguration(config);
        Log("SaveStorageAccountAsync: Configuration saved");
        
        // Connect immediately
        await _blobService.ConnectWithTokenAsync(
            config.BlobServiceUri!, 
            bucketName, 
            _tokenProvider);
        Log("SaveStorageAccountAsync: Connected to Azure storage");
    }
    
    /// <summary>
    /// Gets whether the user is currently authenticated with Entra ID.
    /// </summary>
    public bool IsEntraIdAuthenticated => _tokenProvider != null;

    /// <summary>
    /// Tests the connection using a connection string.
    /// Use this for personal Microsoft accounts.
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionStringAsync(
        string connectionString, string bucketName)
    {
        Log($"TestConnectionStringAsync: Testing connection string to container {bucketName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        
        var result = await _blobService.TestConnectionAsync(connectionString, bucketName);
        Log($"TestConnectionStringAsync: Result success={result.success}");
        return result;
    }

    /// <summary>
    /// Saves and connects using a connection string (encrypts it before storing).
    /// Use this for personal Microsoft accounts.
    /// </summary>
    public async Task SaveConnectionStringAsync(string connectionString, string bucketName)
    {
        Log($"SaveConnectionStringAsync: Saving connection string config for container {bucketName}");
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);

        if (!_encryptionService.IsInitialized)
            throw new InvalidOperationException("Encryption service must be initialized before saving connection string.");

        // Connection strings often contain AccountKey=... credentials. Hold the
        // plaintext UTF-8 bytes only as long as Encrypt needs them, then zero
        // the buffer so a subsequent heap dump (or GC compaction copy) cannot
        // recover the secret. We can't zero the inbound `string` parameter
        // itself (C# strings are immutable in managed memory), but we can at
        // least keep our own copy of the secret out of the reachable heap.
        var plaintext = System.Text.Encoding.UTF8.GetBytes(connectionString);
        byte[] encrypted;
        try
        {
            encrypted = _encryptionService.Encrypt(plaintext);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }

        var config = _databaseService.GetConfiguration();
        config.EncryptedConnectionString = encrypted;
        config.ContainerName = bucketName;
        config.AuthMethod = StorageAuthMethod.ConnectionString;
        config.IsEntraIdAuthenticated = false;
        _databaseService.SaveConfiguration(config);
        Log("SaveConnectionStringAsync: Encrypted connection string saved");

        // Connect immediately
        await _blobService.ConnectAsync(connectionString, bucketName);
        Log("SaveConnectionStringAsync: Connected to Azure storage");
    }
}
