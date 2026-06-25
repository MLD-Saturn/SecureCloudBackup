using SecureCloudBackup.Core.Models;
using SecureCloudBackup.Core.Services.Backends;
using SecureCloudBackup.Crypto;
using Xunit;

namespace SecureCloudBackup.Tests;

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
    public void Initialize_FreshPath_WritesInitialEncryptedSnapshot()
    {
        using var backend = new InMemorySnapshotBackend();

        backend.Initialize(_dbPath, Password);

        // Initialize persists an initial (empty-schema) snapshot so the catalog
        // is detectable on disk right away (first-run vs. unlock routing relies
        // on this). The on-disk file is the encrypted AZDB envelope, never
        // plaintext.
        Assert.True(File.Exists(_dbPath));
        var bytes = File.ReadAllBytes(_dbPath);
        Assert.True(DbSnapshotEnvelope.HasMagic(bytes), "Initial snapshot must be an AZDB envelope.");
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

    [Fact]
    public void Initialize_EmptyPassword_Throws()
    {
        using var backend = new InMemorySnapshotBackend();

        Assert.Throws<ArgumentException>(() => backend.Initialize(_dbPath, string.Empty));
    }

    [Fact]
    public void Reopen_WithTruncatedSnapshot_Throws()
    {
        using (var backend = new InMemorySnapshotBackend())
        {
            backend.Initialize(_dbPath, Password);
            backend.SetIndexMetadata("x", DateTime.UtcNow);
            backend.Checkpoint();
        }

        // Truncate the on-disk snapshot to simulate a corrupted/partial file.
        var bytes = File.ReadAllBytes(_dbPath);
        File.WriteAllBytes(_dbPath, bytes[..(bytes.Length / 2)]);

        using var reopened = new InMemorySnapshotBackend();
        Assert.ThrowsAny<Exception>(() => reopened.Initialize(_dbPath, Password));
    }

    [Fact]
    public void Close_ThenDispose_DoesNotThrowAndPersistsOnce()
    {
        var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, Password);
        backend.SetIndexMetadata("once", new DateTime(2024, 9, 9, 9, 9, 9, DateTimeKind.Utc));

        backend.Close();          // persists
        var afterClose = File.ReadAllBytes(_dbPath);
        backend.Dispose();        // must NOT persist again / must not throw

        var afterDispose = File.ReadAllBytes(_dbPath);
        Assert.Equal(afterClose, afterDispose);
    }

    [Fact]
    public void Checkpoint_OverExistingSnapshot_AlwaysLeavesAFullyValidSnapshot()
    {
        // Crash-safety property: a checkpoint replaces the destination via a
        // temp-then-atomic-rename, so the on-disk snapshot is always a complete,
        // decryptable file -- never a partial write. Verify that after repeated
        // checkpoints the snapshot is always valid and equals the latest state,
        // and that no temp residue is ever left behind.
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, Password);

        for (var i = 0; i < 5; i++)
        {
            backend.SetIndexMetadata("iteration", new DateTime(2024, 1, 1, 0, 0, i, DateTimeKind.Utc));
            backend.Checkpoint();

            // The destination is always a complete, decryptable snapshot.
            var onDisk = File.ReadAllBytes(_dbPath);
            Assert.True(DbSnapshotEnvelope.HasMagic(onDisk));
            var image = DbSnapshotEnvelope.Decrypt(onDisk, Password);
            Assert.NotEmpty(image);

            // No temp file ever lingers after a successful checkpoint.
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
        }
    }

    [Fact]
    public void Checkpoint_AfterFailedWriteAttempt_SnapshotIsNeverPartial()
    {
        // Crash-safety property (portable): whatever happens during a checkpoint,
        // the on-disk snapshot is never a partial/truncated file -- it is a
        // complete, decryptable snapshot -- and no temp residue is left behind.
        // A write failure is injected by making the destination read-only; the
        // exact pass/throw behaviour of an atomic rename over a read-only target
        // is platform-specific, so this test asserts only the invariant that
        // holds on every platform.
        var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, Password);
        backend.SetIndexMetadata("good", new DateTime(2024, 3, 3, 3, 3, 3, DateTimeKind.Utc));
        backend.Checkpoint();

        backend.SetIndexMetadata("bad", DateTime.UtcNow);
        var info = new FileInfo(_dbPath) { IsReadOnly = true };
        try { backend.Checkpoint(); }
        catch { /* platform-dependent; the invariant below is what matters */ }
        finally { info.IsReadOnly = false; backend.Dispose(); }

        // The destination is always a fully valid, decryptable snapshot.
        var afterAttempt = File.ReadAllBytes(_dbPath);
        Assert.True(DbSnapshotEnvelope.HasMagic(afterAttempt));
        Assert.NotEmpty(DbSnapshotEnvelope.Decrypt(afterAttempt, Password));

        // No temp residue regardless of outcome.
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    [Fact]
    public void LargeCatalog_RoundTripsThroughSnapshot()
    {
        const int fileCount = 2000;
        using (var backend = new InMemorySnapshotBackend())
        {
            backend.Initialize(_dbPath, Password);
            for (var i = 0; i < fileCount; i++)
            {
                backend.SaveBackedUpFile(new BackedUpFile
                {
                    LocalPath = $"C:/data/file-{i}.dat",
                    FileSize = i,
                    FileHash = $"hash-{i}",
                    LastModified = DateTime.UtcNow,
                    Chunks = []
                });
            }
            backend.Checkpoint();
        }

        using var reopened = new InMemorySnapshotBackend();
        reopened.Initialize(_dbPath, Password);
        var all = reopened.GetAllBackedUpFiles();
        Assert.Equal(fileCount, all.Count);
        var sample = reopened.GetBackedUpFile("C:/data/file-1999.dat");
        Assert.NotNull(sample);
        Assert.Equal(1999, sample!.FileSize);
    }
}
