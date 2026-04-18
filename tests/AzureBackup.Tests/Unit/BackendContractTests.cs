using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Option C / C-1 final (step a): contract tests run identically against
/// every <see cref="IDatabaseBackend"/> implementation. Each test uses
/// xUnit's <c>[Theory]</c> + <c>[ClassData]</c> to repeat itself once per
/// backend and asserts only the cross-backend-common shape.
///
/// <para>
/// What this proves:
/// </para>
/// <list type="bullet">
///   <item>SqliteBackend and LiteDbBackend agree on the public surface
///     they both expose - same method signatures, same return shapes,
///     same persistence semantics.</item>
///   <item>Any future change to the interface that breaks one backend
///     fails compilation here too, so divergence cannot creep in
///     unnoticed.</item>
///   <item>Lays the foundation for the C-3 benchmarks: the same
///     scenario can be timed against both backends with no test code
///     duplication.</item>
/// </list>
///
/// <para>
/// What this does NOT prove:
/// </para>
/// <list type="bullet">
///   <item>Behavioural <i>equivalence</i> beyond the shape we assert here.
///     Backends are allowed to differ on unobservable details (e.g. the
///     exact internal layout of ChunkIndexEntry.ReferencingFiles -
///     populated on LiteDB, empty on SQLite).</item>
///   <item>Performance parity. That's C-3.</item>
/// </list>
/// </summary>
public class BackendContractTests
{
    /// <summary>
    /// Provides a freshly-constructed backend instance per test. Each
    /// factory function creates a brand new backend in a brand new temp
    /// directory so tests are fully isolated even when xUnit runs them in
    /// parallel within the same class.
    /// </summary>
    internal sealed class BackendFactories : TheoryData<string, Func<string, IDatabaseBackend>>
    {
        public BackendFactories()
        {
            Add("SqliteBackend", _ => new SqliteBackend());
            Add("LiteDbBackend", _ => new LiteDbBackend());
        }
    }

    /// <summary>
    /// Convenience: open a fresh backend in a temp dir, return a tuple
    /// the test can dispose at the end.
    /// </summary>
    private static (IDatabaseBackend Backend, string DbPath, string TestDir) Open(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "azbk-contract-" + label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "contract.db");
        var backend = factory(label);
        backend.Initialize(dbPath, "ContractTestPwd!".AsSpan());
        return (backend, dbPath, dir);
    }

    private static void Cleanup(IDatabaseBackend backend, string testDir)
    {
        backend.Dispose();
        try { Directory.Delete(testDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void Initialize_FreshDb_IsInitializedTrue(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, dbPath, dir) = Open(label, factory);
        try
        {
            Assert.True(backend.IsInitialized);
            Assert.Equal(dbPath, backend.DatabasePath);
        }
        finally { Cleanup(backend, dir); }
    }

    /// <summary>
    /// Asserts two nullable DateTimes represent the same instant. LiteDB
    /// strips DateTimeKind on persist (UTC values come back as Local with
    /// the timezone offset baked in); SQLite preserves the kind. Comparing
    /// as instants matches what every production consumer of these values
    /// actually does (every comparison goes through .ToUniversalTime()
    /// or treats the value as an opaque ordered token).
    /// </summary>
    private static void AssertSameInstant(DateTime? expected, DateTime? actual)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (expected.HasValue)
        {
            Assert.Equal(expected.Value.ToUniversalTime(), actual!.Value.ToUniversalTime());
        }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void Configuration_RoundTripsScalarFields(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, _, dir) = Open(label, factory);
        try
        {
            var saved = new BackupConfiguration
            {
                ContainerName = "contract-container",
                TotalBytesUploaded = 1_000_000,
                LastBackupTime = new DateTime(2026, 4, 17, 22, 0, 0, DateTimeKind.Utc),
            };
            backend.SaveConfiguration(saved);

            var loaded = backend.GetConfiguration();
            Assert.Equal(saved.ContainerName, loaded.ContainerName);
            Assert.Equal(saved.TotalBytesUploaded, loaded.TotalBytesUploaded);
            AssertSameInstant(saved.LastBackupTime, loaded.LastBackupTime);
        }
        finally { Cleanup(backend, dir); }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void IndexMetadata_RoundTrip(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, _, dir) = Open(label, factory);
        try
        {
            Assert.Null(backend.GetIndexMetadata("missing"));
            var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
            backend.SetIndexMetadata("k", when);
            AssertSameInstant(when, backend.GetIndexMetadata("k"));
        }
        finally { Cleanup(backend, dir); }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void BackedUpFile_RoundTripWithChunks(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, _, dir) = Open(label, factory);
        try
        {
            var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
            var saved = new BackedUpFile
            {
                LocalPath = @"C:\contract\file.bin",
                BlobName = "metadata/file.bin.json",
                FileSize = 4096,
                LastModified = when,
                FileHash = "FILEHASH",
                Status = BackupStatus.Completed,
                BackedUpAt = when,
                MetadataVersion = 1,
                Chunks =
                {
                    new ChunkInfo { Index = 0, Offset = 0,    Length = 2048, Hash = "C1", BlobName = "chunks/C1" },
                    new ChunkInfo { Index = 1, Offset = 2048, Length = 2048, Hash = "C2", BlobName = "chunks/C2" },
                },
            };
            backend.SaveBackedUpFile(saved);

            var loaded = backend.GetBackedUpFile(saved.LocalPath);
            Assert.NotNull(loaded);
            Assert.Equal(saved.LocalPath, loaded!.LocalPath);
            Assert.Equal(saved.FileSize, loaded.FileSize);
            Assert.Equal(saved.Status, loaded.Status);
            Assert.Equal(2, loaded.Chunks.Count);
            Assert.Equal("C1", loaded.Chunks[0].Hash);
            Assert.Equal("C2", loaded.Chunks[1].Hash);
        }
        finally { Cleanup(backend, dir); }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void GetBackedUpFile_MissingPath_ReturnsNull(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, _, dir) = Open(label, factory);
        try
        {
            Assert.Null(backend.GetBackedUpFile(@"C:\does\not\exist"));
        }
        finally { Cleanup(backend, dir); }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void ChunkIndex_SaveGetDelete(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, _, dir) = Open(label, factory);
        try
        {
            var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
            var entry = new ChunkIndexEntry
            {
                ChunkHash = "HASH",
                FirstUploadedAt = when,
                OriginalUploaderPath = @"C:\src\hash",
                SizeBytes = 1024,
                ReferenceCount = 3,
                CurrentTier = StorageTier.Cool,
                LastVerifiedAt = when,
            };
            backend.SaveChunkIndexEntry(entry);

            var loaded = backend.GetChunkIndexEntry("HASH");
            Assert.NotNull(loaded);
            Assert.Equal(3, loaded!.ReferenceCount);
            Assert.Equal(StorageTier.Cool, loaded.CurrentTier);
            Assert.Equal(1024, loaded.SizeBytes);

            backend.DeleteChunkIndexEntry("HASH");
            Assert.Null(backend.GetChunkIndexEntry("HASH"));
        }
        finally { Cleanup(backend, dir); }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void ChunkIndex_BulkInsertCount(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, _, dir) = Open(label, factory);
        try
        {
            var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
            var entries = Enumerable.Range(0, 50).Select(i => new ChunkIndexEntry
            {
                ChunkHash = $"H{i:D4}",
                FirstUploadedAt = when,
                OriginalUploaderPath = $@"C:\src\h{i}",
                SizeBytes = 1024,
                ReferenceCount = 1,
                CurrentTier = StorageTier.Hot,
                LastVerifiedAt = when,
            }).ToList();

            backend.BulkInsertChunkIndexEntries(entries);

            Assert.Equal(50, backend.GetChunkIndexCount());
        }
        finally { Cleanup(backend, dir); }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void ChunkIndex_OrphanFilter(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, _, dir) = Open(label, factory);
        try
        {
            var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
            backend.SaveChunkIndexEntry(new ChunkIndexEntry
            {
                ChunkHash = "ALIVE", FirstUploadedAt = when,
                ReferenceCount = 1, LastVerifiedAt = when,
            });
            backend.SaveChunkIndexEntry(new ChunkIndexEntry
            {
                ChunkHash = "DEAD", FirstUploadedAt = when,
                ReferenceCount = 0, LastVerifiedAt = when,
            });

            var orphans = backend.GetOrphanedChunks();

            Assert.Single(orphans);
            Assert.Equal("DEAD", orphans[0].ChunkHash);
        }
        finally { Cleanup(backend, dir); }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void PendingChanges_QueueAndDrain(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, _, dir) = Open(label, factory);
        try
        {
            var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
            backend.QueueFileChange(new FileChangeEvent
            {
                FilePath = @"C:\a.txt", ChangeType = FileChangeType.Created, DetectedAt = when,
            });
            backend.QueueFileChange(new FileChangeEvent
            {
                FilePath = @"C:\b.txt", ChangeType = FileChangeType.Modified, DetectedAt = when.AddSeconds(1),
            });

            var batch = backend.GetPendingChanges();
            Assert.Equal(2, batch.Count);
            Assert.Equal(@"C:\a.txt", batch[0].FilePath); // ordered by DetectedAt ASC

            backend.RemovePendingChange(@"C:\a.txt");
            Assert.Single(backend.GetPendingChanges());
        }
        finally { Cleanup(backend, dir); }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void Statistics_AggregatesByStatus(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, _, dir) = Open(label, factory);
        try
        {
            var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
            BackedUpFile File(string p, BackupStatus s, long size) => new()
            {
                LocalPath = p, FileSize = size, Status = s,
                LastModified = when, BackedUpAt = when,
            };
            backend.SaveBackedUpFile(File(@"C:\done.bin", BackupStatus.Completed, 1000));
            backend.SaveBackedUpFile(File(@"C:\pend.bin", BackupStatus.Pending,   2000));
            backend.SaveBackedUpFile(File(@"C:\fail.bin", BackupStatus.Failed,    3000));

            var stats = backend.GetStatistics();
            Assert.Equal(3, stats.TotalFiles);
            Assert.Equal(6000, stats.TotalSize);
            Assert.Equal(1, stats.CompletedFiles);
            Assert.Equal(1, stats.PendingFiles);
            Assert.Equal(1, stats.FailedFiles);
        }
        finally { Cleanup(backend, dir); }
    }

    [Theory, ClassData(typeof(BackendFactories))]
    internal void Persistence_SurvivesReopen(
        string label, Func<string, IDatabaseBackend> factory)
    {
        var (backend, dbPath, dir) = Open(label, factory);
        try
        {
            var when = new DateTime(2026, 4, 17, 21, 30, 45, DateTimeKind.Utc);
            backend.SetIndexMetadata("k", when);
            backend.Dispose();

            using var reopened = factory(label);
            reopened.Initialize(dbPath, "ContractTestPwd!".AsSpan());
            AssertSameInstant(when, reopened.GetIndexMetadata("k"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
