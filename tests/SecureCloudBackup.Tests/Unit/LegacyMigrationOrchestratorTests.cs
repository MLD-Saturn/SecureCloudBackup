using SecureCloudBackup.Core.Services.Backends;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Unit tests for the input-validation guards of
/// <see cref="LegacyMigrationOrchestrator.Migrate"/>. These cover the
/// deterministic early-exit paths that protect the original catalog BEFORE any
/// helper process is launched (empty password, missing database, missing salt
/// sidecar). The full cross-process migration round-trip is intentionally not
/// covered here: this test assembly references the SQLCipher engine, so opening
/// a migrated snapshot in-process would put both SQLite engines in one process,
/// which is impossible by design; that path belongs to the two-exe integration
/// phase.
/// </summary>
public sealed class LegacyMigrationOrchestratorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public LegacyMigrationOrchestratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "azbk-mig-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "backup.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Migrate_EmptyPassword_ThrowsArgumentException()
    {
        File.WriteAllBytes(_dbPath, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(_dbPath + ".salt", new byte[16]);

        Assert.Throws<ArgumentException>(() => LegacyMigrationOrchestrator.Migrate(_dbPath, ReadOnlySpan<char>.Empty));
    }

    [Fact]
    public void Migrate_WhitespacePath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => LegacyMigrationOrchestrator.Migrate("   ", "pw".AsSpan()));
    }

    [Fact]
    public void Migrate_MissingDatabaseFile_ThrowsLegacyMigrationException()
    {
        // Salt sidecar present, but no database file.
        File.WriteAllBytes(_dbPath + ".salt", new byte[16]);

        Assert.Throws<LegacyMigrationException>(() => LegacyMigrationOrchestrator.Migrate(_dbPath, "pw".AsSpan()));
    }

    [Fact]
    public void Migrate_MissingSaltSidecar_ThrowsLegacyMigrationException()
    {
        // Database file present, but no salt sidecar.
        File.WriteAllBytes(_dbPath, new byte[] { 1, 2, 3, 4 });

        Assert.Throws<LegacyMigrationException>(() => LegacyMigrationOrchestrator.Migrate(_dbPath, "pw".AsSpan()));
    }

    [Fact]
    public void Migrate_MissingDatabase_LeavesSaltSidecarUntouched()
    {
        var saltPath = _dbPath + ".salt";
        File.WriteAllBytes(saltPath, new byte[16]);

        Assert.Throws<LegacyMigrationException>(() => LegacyMigrationOrchestrator.Migrate(_dbPath, "pw".AsSpan()));

        // The guard must fail before touching anything on disk.
        Assert.True(File.Exists(saltPath));
        Assert.False(File.Exists(_dbPath));
    }
}
