using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using AzureBackup.Crypto;
using AzureBackup.Migration;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for <see cref="LegacyCatalogMigrator"/>: the single-engine migration
/// helper that reads a legacy SQLCipher catalog and writes the new AES-256-GCM
/// encrypted snapshot. Seeds a real SQLCipher database via <see cref="SqliteBackend"/>
/// (the test process runs the SQLCipher engine), migrates it, and asserts the
/// snapshot decrypts and contains the original data.
/// </summary>
public sealed class LegacyCatalogMigratorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _legacyDbPath;
    private readonly string _outputSnapshotPath;
    private const string Password = "legacy-migrate-password";

    public LegacyCatalogMigratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "azbk-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _legacyDbPath = Path.Combine(_dir, "legacy.db");
        _outputSnapshotPath = Path.Combine(_dir, "backup.db.snapshot");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (BackedUpFile file, ChunkIndexEntry chunk) SeedLegacyDatabase()
    {
        BackedUpFile file;
        ChunkIndexEntry chunk;
        using (var backend = new SqliteBackend())
        {
            backend.Initialize(_legacyDbPath, Password.AsSpan());

            file = new BackedUpFile
            {
                LocalPath = "C:/docs/quarterly-report.xlsx",
                FileSize = 987654,
                FileHash = "sha256-deadbeef",
                LastModified = new DateTime(2024, 2, 2, 2, 2, 2, DateTimeKind.Utc),
                Chunks = []
            };
            backend.SaveBackedUpFile(file);

            chunk = new ChunkIndexEntry
            {
                ChunkHash = "chunk-hash-0001",
                SizeBytes = 65536,
                ReferenceCount = 1,
                CurrentTier = StorageTier.Hot
            };
            backend.SaveChunkIndexEntry(chunk);
        }
        return (file, chunk);
    }

    private string ReadSaltBase64()
        => Convert.ToBase64String(File.ReadAllBytes(_legacyDbPath + ".salt"));

    [Fact]
    public void Migrate_LegacySqlCipherCatalog_WritesDecryptableSnapshot()
    {
        SeedLegacyDatabase();
        var request = new MigrationRequest(_legacyDbPath, ReadSaltBase64(), Password, _outputSnapshotPath);

        LegacyCatalogMigrator.Migrate(request);

        Assert.True(File.Exists(_outputSnapshotPath));
        var bytes = File.ReadAllBytes(_outputSnapshotPath);
        Assert.True(DbSnapshotEnvelope.HasMagic(bytes), "Output must be an AZDB snapshot.");
        // Decrypts with the same password.
        var image = DbSnapshotEnvelope.Decrypt(bytes, Password);
        Assert.NotEmpty(image);
    }

    [Fact]
    public void Migrate_SnapshotOpensInTheModernBackend_WithSameData()
    {
        var (seededFile, seededChunk) = SeedLegacyDatabase();
        var request = new MigrationRequest(_legacyDbPath, ReadSaltBase64(), Password, _outputSnapshotPath);

        LegacyCatalogMigrator.Migrate(request);

        // The migrated snapshot must open in the new in-memory snapshot backend
        // and contain the seeded data verbatim.
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_outputSnapshotPath, Password.AsSpan());

        var file = backend.GetBackedUpFile(seededFile.LocalPath);
        Assert.NotNull(file);
        Assert.Equal(seededFile.FileSize, file!.FileSize);
        Assert.Equal(seededFile.FileHash, file.FileHash);

        var chunk = backend.GetChunkIndexEntry(seededChunk.ChunkHash);
        Assert.NotNull(chunk);
        Assert.Equal(seededChunk.SizeBytes, chunk!.SizeBytes);
    }

    [Fact]
    public void Migrate_WithWrongPassword_ThrowsInvalidPassword()
    {
        SeedLegacyDatabase();
        var request = new MigrationRequest(_legacyDbPath, ReadSaltBase64(), "wrong-password", _outputSnapshotPath);

        var ex = Assert.Throws<MigrationException>(() => LegacyCatalogMigrator.Migrate(request));
        Assert.Equal(MigrationExitCode.InvalidPassword, ex.ExitCode);
        Assert.False(File.Exists(_outputSnapshotPath), "No snapshot should be written on a wrong password.");
    }

    [Fact]
    public void Migrate_WithMissingLegacyDatabase_ThrowsSourceNotFound()
    {
        var request = new MigrationRequest(
            Path.Combine(_dir, "does-not-exist.db"),
            Convert.ToBase64String(new byte[KdfParameters.SaltSize]),
            Password,
            _outputSnapshotPath);

        var ex = Assert.Throws<MigrationException>(() => LegacyCatalogMigrator.Migrate(request));
        Assert.Equal(MigrationExitCode.SourceNotFound, ex.ExitCode);
    }

    [Fact]
    public void Migrate_WithBadSaltLength_ThrowsBadRequest()
    {
        SeedLegacyDatabase();
        var request = new MigrationRequest(
            _legacyDbPath,
            Convert.ToBase64String(new byte[8]), // wrong length
            Password,
            _outputSnapshotPath);

        var ex = Assert.Throws<MigrationException>(() => LegacyCatalogMigrator.Migrate(request));
        Assert.Equal(MigrationExitCode.BadRequest, ex.ExitCode);
    }

    [Fact]
    public void Migrate_WithNonBase64Salt_ThrowsBadRequest()
    {
        SeedLegacyDatabase();
        var request = new MigrationRequest(_legacyDbPath, "not!base64!", Password, _outputSnapshotPath);

        var ex = Assert.Throws<MigrationException>(() => LegacyCatalogMigrator.Migrate(request));
        Assert.Equal(MigrationExitCode.BadRequest, ex.ExitCode);
    }

    [Fact]
    public void Migrate_WithEmptyPassword_ThrowsBadRequest()
    {
        SeedLegacyDatabase();
        var request = new MigrationRequest(_legacyDbPath, ReadSaltBase64(), string.Empty, _outputSnapshotPath);

        var ex = Assert.Throws<MigrationException>(() => LegacyCatalogMigrator.Migrate(request));
        Assert.Equal(MigrationExitCode.BadRequest, ex.ExitCode);
    }
}
