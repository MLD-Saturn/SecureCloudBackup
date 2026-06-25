using SecureCloudBackup.Core.Services;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Pins the provider-neutral authenticator seam introduced when the interactive
/// sign-in flow moved out of <see cref="BackupOrchestrator"/> into an injected
/// <see cref="IStorageAuthenticator"/> (and the <c>TokenCredential</c> field was
/// removed). The orchestrator now delegates sign-in to the authenticator and
/// caches the returned provider-neutral <see cref="IStorageTokenProvider"/>.
/// These tests use a NON-Azure fake authenticator + token provider, which also
/// proves the orchestrator no longer depends on any Azure type for auth.
/// </summary>
public class OrchestratorAuthenticatorTests : IAsyncLifetime
{
    private string _testDirectory = null!;
    private string _dbPath = null!;
    private EncryptionService _encryptionService = null!;
    private InMemoryBlobService _blobService = null!;
    private LocalDatabaseService _databaseService = null!;
    private FileWatcherService _fileWatcherService = null!;
    private BackupOrchestrator? _orchestrator;

    private const string TestPassword = "AuthSeamTestPassword123!";

    public Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"AuthSeamTests_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_testDirectory, "test.db");
        Directory.CreateDirectory(_testDirectory);

        _encryptionService = new EncryptionService();
        _blobService = new InMemoryBlobService(_encryptionService);
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, TestPassword);
        _fileWatcherService = new FileWatcherService(_databaseService);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_orchestrator != null)
        {
            try { await _orchestrator.DisposeAsync(); } catch { /* best effort */ }
        }
        try { _encryptionService.Dispose(); } catch { /* best effort */ }
        try { _databaseService.Dispose(); } catch (NullReferenceException) { }

        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); } catch { /* ignore cleanup errors */ }
        }
    }

    private BackupOrchestrator CreateOrchestrator(IStorageAuthenticator? authenticator)
    {
        _orchestrator = new BackupOrchestrator(
            _databaseService, _encryptionService, new ChunkingService(),
            _blobService, _fileWatcherService, authenticator);
        return _orchestrator;
    }

    [Fact]
    public async Task AuthenticateWithEntraIdAsync_NoAuthenticator_ReturnsUnavailableAndStaysSignedOut()
    {
        var orchestrator = CreateOrchestrator(authenticator: null);

        var (success, _) = await orchestrator.AuthenticateWithEntraIdAsync();

        Assert.False(success);
        Assert.False(orchestrator.IsEntraIdAuthenticated);
    }

    [Fact]
    public async Task AuthenticateWithEntraIdAsync_AuthenticatorSucceeds_SignsInAndPersistsConfig()
    {
        var orchestrator = CreateOrchestrator(
            new FakeAuthenticator(new StorageAuthenticationResult(true, "ok", new StubTokenProvider())));

        var (success, _) = await orchestrator.AuthenticateWithEntraIdAsync();

        Assert.True(success);
        Assert.True(orchestrator.IsEntraIdAuthenticated);
        Assert.True(_databaseService.GetConfiguration().IsEntraIdAuthenticated);
    }

    [Fact]
    public async Task AuthenticateWithEntraIdAsync_AuthenticatorFails_StaysSignedOut()
    {
        var orchestrator = CreateOrchestrator(
            new FakeAuthenticator(new StorageAuthenticationResult(false, "denied", null)));

        var (success, _) = await orchestrator.AuthenticateWithEntraIdAsync();

        Assert.False(success);
        Assert.False(orchestrator.IsEntraIdAuthenticated);
    }

    private sealed class FakeAuthenticator : IStorageAuthenticator
    {
        private readonly StorageAuthenticationResult _result;
        public FakeAuthenticator(StorageAuthenticationResult result) => _result = result;

        public Task<StorageAuthenticationResult> AuthenticateInteractiveAsync(CancellationToken cancellationToken, int timeoutSeconds)
            => Task.FromResult(_result);
    }

    private sealed class StubTokenProvider : IStorageTokenProvider
    {
        public ValueTask<StorageAccessToken> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken = default)
            => new(new StorageAccessToken("token", DateTimeOffset.UtcNow.AddHours(1)));
    }
}
