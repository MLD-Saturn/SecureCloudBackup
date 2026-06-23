using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using AzureBackup.Crypto;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for <see cref="InMemorySnapshotBackend"/>: the in-memory SQLite +
/// AES-256-GCM encrypted snapshot backend that replaces SQLCipher. Verifies the
/// open / first-run / persist+reopen round-trip, that a wrong password is
/// rejected, and that the on-disk artifact is the encrypted snapshot (never a
/// plaintext SQLite file).
/// </summary>
public sealed class InMemorySnapshotBackendTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private const string Password = "snapshot-test-password";

    public InMemorySnapshotBackendTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "azbk-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "backup.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Initialize_FreshPath_CreatesSchemaAndIsInitialized()
    {
        using var backend = new InMemorySnapshotBackend();

        backend.Initialize(_dbPath, Password);

        Assert.True(backend.IsInitialized);
        // A fresh catalog returns a default configuration, not null.
        Assert.NotNull(backend.GetConfiguration());
    }

    [Fact]
    public void Initialize_FreshPath_DoesNotWriteSnapshotUntilCheckpoint()
    {
        using var backend = new InMemorySnapshotBackend();

        backend.Initialize(_dbPath, Password);

        // Nothing persisted yet -- the in-memory DB exists but no snapshot file.
        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public void Checkpoint_WritesEncryptedSnapshotFile()
    {
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, Password);

        backend.Checkpoint();

        Assert.True(File.Exists(_dbPath));
        var bytes = File.ReadAllBytes(_dbPath);
        Assert.True(DbSnapshotEnvelope.HasMagic(bytes), "On-disk file must be an AZDB snapshot.");
    }

    [Fact]
    public void Checkpoint_OnDiskFileIsNotPlaintextSqlite()
    {
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, Password);
        // Write some recognizable data.
        backend.SetIndexMetadata("marker", new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        backend.Checkpoint();

        var header = new byte[16];
        using (var fs = File.OpenRead(_dbPath)) { fs.ReadExactly(header, 0, 16); }
        var headerText = System.Text.Encoding.ASCII.GetString(header);
        Assert.False(headerText.StartsWith("SQLite format 3"),
            "Snapshot must not be a plaintext SQLite file.");
    }

    [Fact]
    public void PersistThenReopen_WithSamePassword_RestoresData()
    {
        var expected = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        using (var backend = new InMemorySnapshotBackend())
        {
            backend.Initialize(_dbPath, Password);
            backend.SetIndexMetadata("built-at", expected);
            backend.Checkpoint();
        }

        using (var reopened = new InMemorySnapshotBackend())
        {
            reopened.Initialize(_dbPath, Password);
            var actual = reopened.GetIndexMetadata("built-at");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Close_PersistsAutomatically_SoReopenSeesData()
    {
        var file = new BackedUpFile
        {
            LocalPath = "C:/data/report.docx",
            FileSize = 4242,
            FileHash = "abc123",
            LastModified = DateTime.UtcNow,
            Chunks = []
        };

        using (var backend = new InMemorySnapshotBackend())
        {
            backend.Initialize(_dbPath, Password);
            backend.SaveBackedUpFile(file);
            // No explicit Checkpoint -- Close must persist.
        }

        using (var reopened = new InMemorySnapshotBackend())
        {
            reopened.Initialize(_dbPath, Password);
            var loaded = reopened.GetBackedUpFile("C:/data/report.docx");
            Assert.NotNull(loaded);
            Assert.Equal(4242, loaded!.FileSize);
        }
    }

    [Fact]
    public void Reopen_WithWrongPassword_Throws()
    {
        using (var backend = new InMemorySnapshotBackend())
        {
            backend.Initialize(_dbPath, Password);
            backend.SetIndexMetadata("x", DateTime.UtcNow);
            backend.Checkpoint();
        }

        using var reopened = new InMemorySnapshotBackend();
        Assert.ThrowsAny<Exception>(() => reopened.Initialize(_dbPath, "wrong-password"));
    }

    [Fact]
    public void SecureReset_DeletesSnapshotFile()
    {
        var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, Password);
        backend.Checkpoint();
        Assert.True(File.Exists(_dbPath));

        backend.SecureReset();

        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public void Checkpoint_Twice_LeavesNoTempFilesBehind()
    {
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, Password);
        backend.SetIndexMetadata("a", DateTime.UtcNow);
        backend.Checkpoint();
        backend.SetIndexMetadata("b", DateTime.UtcNow);
        backend.Checkpoint();

        var temps = Directory.GetFiles(_dir, "*.tmp-*");
        Assert.Empty(temps);
    }
}
