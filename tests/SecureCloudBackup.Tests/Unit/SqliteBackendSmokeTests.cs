using SecureCloudBackup.Core;
using SecureCloudBackup.Core.Models;
using SecureCloudBackup.Core.Services;
using SecureCloudBackup.Core.Services.Backends;
using SecureCloudBackup.Crypto;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Option C / C-1a: end-to-end smoke test for the SQLite + SQLCipher
/// integration. Proves the encryption stack works on .NET 10 before any
/// real persistence code is written against it.
///
/// <para>
/// What is tested:
/// </para>
/// <list type="bullet">
///   <item>Open an encrypted DB at a fresh path - schema is created, the
///     connection is usable, a salt file appears.</item>
///   <item>Close, reopen with the same password - data is readable, the
///     schema persists.</item>
///   <item>Reopen with the wrong password - throws
///     <see cref="InvalidPasswordException"/> rather than silently returning
///     an empty / corrupt DB.</item>
/// </list>
///
/// <para>
/// These are intentionally small assertions; the full functional surface is
/// proved later by the existing 536 tests once the backend is wired into
/// <c>LocalDatabaseService</c>.
/// </para>
/// </summary>
public class SqliteBackendSmokeTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;

    public SqliteBackendSmokeTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "azbk-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "smoke.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Initialize_ReopenWithSamePassword_Succeeds()
    {
        // Arrange
        const string password = "SmokeTestPassword123!";

        using (var first = new InMemorySnapshotBackend())
        {
            first.Initialize(_dbPath, password.AsSpan());
            Assert.Equal(12, first.CountSchemaTables());
            // first.Dispose() runs here, closing the connection.
        }

        // Act: reopen with the same password.
        using var second = new InMemorySnapshotBackend();
        second.Initialize(_dbPath, password.AsSpan());

        // Assert: schema persisted, no exception, connection works.
        Assert.True(second.IsInitialized);
        Assert.Equal(12, second.CountSchemaTables());
    }

    [Fact]
    public void Initialize_ReopenWithWrongPassword_ThrowsInvalidPassword()
    {
        // Arrange
        const string correctPassword = "CorrectPassword123!";
        const string wrongPassword = "WrongPassword456!";

        using (var first = new InMemorySnapshotBackend())
        {
            first.Initialize(_dbPath, correctPassword.AsSpan());
        }

        // Act + Assert: the snapshot backend must reject the wrong key (the
        // AES-256-GCM tag fails to authenticate) rather than silently opening.
        using var second = new InMemorySnapshotBackend();
        var ex = Record.Exception(() =>
            second.Initialize(_dbPath, wrongPassword.AsSpan()));

        Assert.NotNull(ex);
        // If this fires it tells us what we actually got vs what we expected.
        Assert.True(ex is InvalidPasswordException,
            $"Expected InvalidPasswordException, got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void Initialize_ManyDistinctWrongPasswords_AlwaysThrowInvalidPassword()
    {
        // Regression for the "SQLite Error 11: database disk image is malformed"
        // unlock-screen failure observed by the tester after the B47 LiteDB-probe
        // removal stopped masking the underlying classifier gap.
        //
        // SQLCipher's wrong-key surface is non-deterministic across passwords:
        // the page-1 decrypt produces uniform-random-looking bytes, and the
        // sqlite_master probe in OpenAndUnlockCore can therefore hit any of
        //   * SqliteException(SQLITE_NOTADB = 26) -- "file is not a database"
        //   * SqliteException(SQLITE_CORRUPT = 11) -- "database disk image is
        //     malformed" (was the unmapped surface; now mapped)
        //   * OverflowException / ArgumentOutOfRangeException / IndexOutOfRangeException
        //     -- garbage header values overflow the M.D.Sqlite parsing path
        //
        // All four shapes mean "wrong password" at unlock time. Any path that
        // leaks the raw exception sends the user to the password screen with a
        // baffling error string. Running 50 distinct wrong passwords against the
        // same SQLCipher database is a probabilistically-saturated cover of
        // every garbage-decrypt surface; pre-fix this test failed with the
        // SqliteException(11) leak in well under 50 iterations on typical
        // hardware.
        const string correctPassword = "CorrectClassifierProbe123!";
        using (var first = new InMemorySnapshotBackend())
        {
            first.Initialize(_dbPath, correctPassword.AsSpan());
        }

        var leaks = new List<(int attempt, string type, string message)>();
        for (var i = 0; i < 50; i++)
        {
            var wrong = $"wrong-classifier-probe-{i}-{Guid.NewGuid():N}";
            using var backend = new InMemorySnapshotBackend();
            var ex = Record.Exception(() => backend.Initialize(_dbPath, wrong.AsSpan()));
            if (ex is null)
            {
                leaks.Add((i, "<no exception>", "wrong password silently accepted"));
                continue;
            }
            if (ex is not InvalidPasswordException)
            {
                leaks.Add((i, ex.GetType().Name, ex.Message));
            }
        }

        Assert.True(leaks.Count == 0,
            "Wrong-password classifier let non-InvalidPasswordException shapes leak: " +
            string.Join(" | ", leaks.Select(l => $"#{l.attempt} {l.type}: {l.message}")));
    }

    [Fact]
    public void GetPendingChanges_WithIntMaxValueBatchSize_DoesNotOom()
    {
        // B17 regression: pre-fix, GetPendingChanges(int.MaxValue) tried
        // to allocate List<FileChangeEvent>(2147483647) which throws
        // OutOfMemoryException because List<T> pre-allocates an array
        // of the requested capacity. Real bug observed by tester:
        // CleanupStalePendingChanges called this path during the unlock
        // flow with int.MaxValue as a "no limit" sentinel and OOM'd
        // even on a machine with 34 GB free physical RAM (the failure
        // is per-allocation contiguous block availability, not absolute
        // memory). See B15 stack trace.
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, "PendingPassword123!".AsSpan());

        // Empty pending_changes table is the steady-state for an
        // unlock; the bug fires regardless of row count because the
        // failure is the pre-allocation, not the read.
        var result = backend.GetPendingChanges(int.MaxValue);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ParallelSaveBackedUpFile_DoesNotProduceNestedTransactionOrRaceErrors()
    {
        // B18 regression: drive the same code path the BackupOrchestrator's
        // Parallel.ForEachAsync loop hits at MaxParallelFileBackups=8.
        // Pre-B18 the InWriteLock helper relied on the OUTER null-check
        // staying valid for the duration of the locked section, which is
        // false if a racing Close() ran between the check and
        // EnterWriteLock. The lock itself already serialised the
        // BeginTransaction calls (proven by the architecture review), so
        // the only NEW failure mode this test detects is a concurrent
        // backup workload OOM-ing or throwing nested-transaction errors
        // under heavy contention. With 8 workers x 50 files each = 400
        // SaveBackedUpFile calls in flight, any architectural mistake in
        // the lock handling surfaces here.
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, "ParallelSavePassword123!".AsSpan());

        const int workerCount = 8;
        const int filesPerWorker = 50;
        var workers = Enumerable.Range(0, workerCount).Select(workerId =>
            Task.Run(() =>
            {
                for (var i = 0; i < filesPerWorker; i++)
                {
                    backend.SaveBackedUpFile(new BackedUpFile
                    {
                        LocalPath = $@"C:\test\worker{workerId}\file{i}.bin",
                        BlobName = $"blob/{workerId}/{i}",
                        FileSize = 1024 * (i + 1),
                        LastModified = DateTime.UtcNow,
                        FileHash = $"hash-{workerId}-{i}",
                        Status = BackupStatus.Completed,
                        BackedUpAt = DateTime.UtcNow,
                        Chunks =
                        {
                            new ChunkInfo
                            {
                                Index = 0,
                                Offset = 0,
                                Length = 1024,
                                Hash = $"chunk-{workerId}-{i}",
                                BlobName = $"chunks/{workerId}/{i}",
                            },
                        },
                    });
                }
            })).ToArray();

        await Task.WhenAll(workers);

        // Sanity: every file landed.
        var all = backend.GetAllBackedUpFiles();
        Assert.Equal(workerCount * filesPerWorker, all.Count);
    }

    [Fact]
    public async Task Close_DuringParallelSaveBackedUpFile_DoesNotNullRefOrThrowFromCloseSide()
    {
        // B18 regression: pre-B18 Close() did NOT acquire the write lock,
        // so a racing SaveBackedUpFile could deref a null _connection
        // between its outer null-check and the inner BeginTransaction --
        // producing an opaque NullReferenceException with no
        // diagnostic context. Post-B18 either:
        //   (a) the writer finishes its transaction first and the
        //       Close acquires the lock cleanly, OR
        //   (b) the Close acquires first and the writer sees the
        //       null _connection through the InWriteLock post-lock
        //       re-check, surfacing a typed
        //       InvalidOperationException("Backend was closed before
        //       this writer could run.").
        // Either outcome is acceptable. The forbidden outcomes are
        // NullReferenceException and ObjectDisposedException leaking
        // out of either side.
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, "CloseRacePassword123!".AsSpan());

        var writerExceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        // 4 writer tasks racing the Close on a separate thread.
        var writers = Enumerable.Range(0, 4).Select(workerId =>
            Task.Run(() =>
            {
                for (var i = 0; i < 25; i++)
                {
                    try
                    {
                        backend.SaveBackedUpFile(new BackedUpFile
                        {
                            LocalPath = $@"C:\race\w{workerId}\f{i}.bin",
                            BlobName = $"blob/{workerId}/{i}",
                            FileSize = 100,
                            LastModified = DateTime.UtcNow,
                            FileHash = $"h-{workerId}-{i}",
                            Status = BackupStatus.Completed,
                            BackedUpAt = DateTime.UtcNow,
                        });
                    }
                    catch (InvalidOperationException)
                    {
                        // Acceptable: hit the "backend closed" or
                        // "not initialized" guard after Close.
                    }
                    catch (Exception ex)
                    {
                        writerExceptions.Enqueue(ex);
                    }
                }
            })).ToArray();

        // Give the writers a head start, then close concurrently.
        await Task.Delay(5);
        backend.Close();

        await Task.WhenAll(writers);

        // No NRE / ObjectDisposedException should escape.
        Assert.True(writerExceptions.IsEmpty,
            "Writers leaked unexpected exceptions: " +
            string.Join(" | ", writerExceptions.Select(e => $"{e.GetType().Name}: {e.Message}")));
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_DoNotProduceNestedTransactionOrDriverCorruption()
    {
        // B23 regression: drive the same code path the production
        // BackupOrchestrator hits when its parallel backup loop fans
        // out ChunkIndexService.AddReference to N workers. Each worker
        // alternates between SaveChunkIndexEntry (writer) and
        // GetChunkIndexEntry / GetReferencingFilesForChunk (readers)
        // on the SAME shared SqliteConnection.
        //
        // Pre-B23 the backend serialized only writes; reads were
        // documented as "WAL allows concurrent readers" -- which is
        // true for readers on DIFFERENT connections but FALSE for the
        // single shared connection this backend uses. Production
        // telemetry showed two failure shapes:
        //   1. SQLite Error 1: "cannot start a transaction within a
        //      transaction" -- a reader's open SqliteDataReader
        //      held an implicit transaction that prevented a writer
        //      thread from issuing BEGIN.
        //   2. ArgumentOutOfRangeException ("Index was out of range.
        //      Must be non-negative and less than the size of the
        //      collection. (Parameter 'index')") -- two threads
        //      doing CreateCommand/Dispose corrupted M.D.Sqlite's
        //      internal command-tracker List<>.
        // Both shapes were observed in a single 1000-file production
        // backup run.
        //
        // This test exercises both seams. Writers do an upsert per
        // iteration (BeginTransaction internally via SaveChunkIndexEntry's
        // ON CONFLICT); readers do GetChunkIndexEntry and
        // GetReferencingFilesForChunk -- ExecuteReader paths that
        // pre-B23 were unprotected. Any leaked exception fails the test.
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, "MixedReadWritePassword123!".AsSpan());

        // Seed a known set of chunks so readers always have rows to read.
        const int chunkCount = 50;
        var seed = new List<ChunkIndexEntry>(chunkCount);
        for (var i = 0; i < chunkCount; i++)
        {
            seed.Add(new ChunkIndexEntry
            {
                ChunkHash = $"seed-{i:000}",
                FirstUploadedAt = DateTime.UtcNow,
                OriginalUploaderPath = $@"C:\seed\file{i}.bin",
                SizeBytes = 1024 * (i + 1),
                ReferenceCount = 1,
                CurrentTier = StorageTier.Hot,
                LastVerifiedAt = DateTime.UtcNow,
            });
        }
        backend.BulkInsertChunkIndexEntries(seed);

        const int workerCount = 8;
        const int iterationsPerWorker = 200;
        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        var workers = Enumerable.Range(0, workerCount).Select(workerId =>
            Task.Run(() =>
            {
                var rng = new Random(workerId * 1009);
                for (var i = 0; i < iterationsPerWorker; i++)
                {
                    try
                    {
                        // Half writes, half reads -- mixed in a tight loop
                        // so threads collide on the connection often.
                        if ((i & 1) == 0)
                        {
                            backend.SaveChunkIndexEntry(new ChunkIndexEntry
                            {
                                ChunkHash = $"w{workerId}-{i:000}",
                                FirstUploadedAt = DateTime.UtcNow,
                                OriginalUploaderPath = $@"C:\worker{workerId}\file{i}.bin",
                                SizeBytes = 4096,
                                ReferenceCount = 1,
                                CurrentTier = StorageTier.Hot,
                                LastVerifiedAt = DateTime.UtcNow,
                            });
                        }
                        else
                        {
                            var hash = $"seed-{rng.Next(chunkCount):000}";
                            _ = backend.GetChunkIndexEntry(hash);
                            _ = backend.GetReferencingFilesForChunk(hash);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(ex);
                    }
                }
            })).ToArray();

        await Task.WhenAll(workers);

        Assert.True(exceptions.IsEmpty,
            "Mixed read/write workload leaked exceptions (B23 regression): " +
            string.Join(" | ", exceptions.Select(e => $"{e.GetType().Name}: {e.Message}")));
    }

    [Fact]
    public void CheckDatabaseFileIntegrity_OnHealthyDatabase_ReportsBothPragmasOk()
    {
        // B44: a freshly initialised database must pass both
        // SQLCipher's per-page HMAC check and SQLite's b-tree check.
        // SQLCipher's cipher_integrity_check returns ZERO rows on success
        // (one per failing page otherwise), the opposite of stock SQLite's
        // integrity_check which always returns a single "ok" row on
        // success. The result contract that the Storage Health tab
        // depends on must reflect both shapes correctly.
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, "DiagnosticPassword123!".AsSpan());

        var result = backend.CheckDatabaseFileIntegrity();

        Assert.True(result.CipherOk,
            "cipher_integrity_check reported failures on a fresh DB. Got: " +
            string.Join(" | ", result.CipherIntegrityMessages));
        Assert.True(result.SqliteOk,
            "integrity_check did not return a single 'ok' row on a fresh DB. Got: " +
            string.Join(" | ", result.SqliteIntegrityMessages));
        Assert.True(result.IsHealthy);
    }

    [Fact]
    public void ReindexCorruptIndexes_OnHealthyDatabase_RefusesAndDoesNotRunReindex()
    {
        // B45: the repair path is gated on the diagnosis NOT being
        // healthy. Calling it on a fresh DB must refuse cleanly with
        // an explanatory message rather than silently reindexing
        // every healthy index. This is the contract the Storage
        // Health view's CanAttemptRepair gate relies on.
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, "RepairPassword123!".AsSpan());

        var diagnosis = backend.CheckDatabaseFileIntegrity();
        Assert.True(diagnosis.IsHealthy);

        var repair = backend.ReindexCorruptIndexes(diagnosis);

        Assert.False(repair.WasAttempted);
        Assert.Empty(repair.AttemptedIndexes);
        Assert.Empty(repair.SucceededIndexes);
        Assert.Empty(repair.FailedIndexes);
        Assert.Null(repair.PostRepairDiagnosis);
        Assert.Contains("healthy", repair.RefusalReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Checkpoint_ConcurrentWithWriters_DoesNotProduceDriverOrTransactionErrors()
    {
        // B49 regression: pre-B49 SqliteBackend.Checkpoint() ran the
        // PRAGMA wal_checkpoint(TRUNCATE) statement directly against the
        // shared SqliteConnection without taking the write lock. The
        // hourly checkpoint timer wired by
        // MainWindowViewModel.StartCheckpointTimerIfNotRunning fires
        // from a thread-pool thread, so during a continuous-backup
        // session it could land mid-flight against any of
        // SaveBackedUpFile / BulkInsertChunkIndexEntries /
        // SaveChunkIndexEntry. The B23 architectural fact records the
        // two failure shapes: SQLite Error 1 ("cannot start a
        // transaction within a transaction") and ArgumentOutOfRangeException
        // from M.D.Sqlite's command tracker. A torn checkpoint pass
        // against an in-flight writer can also leave the WAL in a
        // state the next open's recovery cannot reconcile, surfacing
        // on the next unlock as SQLite Error 11 ("database disk image
        // is malformed").
        //
        // This test fans out 8 writer tasks against the same backend
        // and concurrently fires Checkpoint() from a separate task in
        // a tight loop. Pre-B49 this reliably produced one of the two
        // shapes above within a few hundred iterations; post-B49 the
        // checkpoint runs through InWriteLock so writers and the
        // checkpoint pass are strictly serialised.
        using var backend = new InMemorySnapshotBackend();
        backend.Initialize(_dbPath, "CheckpointRacePassword123!".AsSpan());

        const int writerCount = 8;
        const int writesPerWorker = 100;
        const int checkpointPasses = 50;

        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        var writers = Enumerable.Range(0, writerCount).Select(workerId =>
            Task.Run(() =>
            {
                for (var i = 0; i < writesPerWorker; i++)
                {
                    try
                    {
                        backend.SaveBackedUpFile(new BackedUpFile
                        {
                            LocalPath = $@"C:\ckpt\w{workerId}\f{i}.bin",
                            BlobName = $"blob/{workerId}/{i}",
                            FileSize = 4096,
                            LastModified = DateTime.UtcNow,
                            FileHash = $"h-{workerId}-{i}",
                            Status = BackupStatus.Completed,
                            BackedUpAt = DateTime.UtcNow,
                            Chunks =
                            {
                                new ChunkInfo
                                {
                                    Index = 0,
                                    Offset = 0,
                                    Length = 4096,
                                    Hash = $"chunk-{workerId}-{i}",
                                    BlobName = $"chunks/{workerId}/{i}",
                                },
                            },
                        });
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(ex);
                    }
                }
            })).ToArray();

        var checkpointer = Task.Run(() =>
        {
            for (var i = 0; i < checkpointPasses; i++)
            {
                try
                {
                    backend.Checkpoint();
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        });

        await Task.WhenAll(writers.Concat(new[] { checkpointer }));

        Assert.True(exceptions.IsEmpty,
            "Checkpoint racing writers leaked exceptions (B49 regression): " +
            string.Join(" | ", exceptions.Select(e => $"{e.GetType().Name}: {e.Message}")));

        // Sanity: every write landed (the checkpoint did not eat any).
        var all = backend.GetAllBackedUpFiles();
        Assert.Equal(writerCount * writesPerWorker, all.Count);
    }

    [Fact]
    public void QuarantineCorruptDatabase_MovesEveryArtefactUnderTimestampedSuffix_AndPreservesBytes()
    {
        // B50: quarantine moves the catalog file (and its companion
        // -wal / -shm / -journal / .salt files) aside under a
        // timestamped .quarantine-yyyyMMdd-HHmmss suffix. Bytes are
        // PRESERVED -- this is the recovery path where the user did
        // NOT ask to delete data; SecureReset is a separate workflow
        // for that.
        using (var backend = new InMemorySnapshotBackend())
        {
            backend.Initialize(_dbPath, "QuarantinePassword123!".AsSpan());
            backend.SaveBackedUpFile(new BackedUpFile
            {
                LocalPath = @"C:\quarantine\sample.bin",
                BlobName = "blob/sample",
                FileSize = 1024,
                LastModified = DateTime.UtcNow,
                FileHash = "qhash",
                Status = BackupStatus.Completed,
                BackedUpAt = DateTime.UtcNow,
            });
            backend.Checkpoint();
        }

        // Capture pre-quarantine size so we can prove the quarantined
        // bytes are byte-identical to the original. The snapshot format has
        // no .salt sidecar (the salt is embedded in the AZDB envelope), so
        // quarantine only needs to move the single catalog file.
        var dbSize = new FileInfo(_dbPath).Length;
        Assert.True(dbSize > 0, "expected non-empty DB before quarantine");

        var result = LocalDatabaseService.QuarantineCorruptDatabase(_dbPath);

        // Original must be gone (so the next Initialize creates a
        // fresh DB) but the renamed copy must survive.
        Assert.False(File.Exists(_dbPath), "original DB must be moved aside");
        Assert.True(File.Exists(result.QuarantinedDatabasePath), "quarantined DB must exist on disk");
        Assert.Contains(".quarantine-", result.QuarantinedDatabasePath, StringComparison.Ordinal);
        Assert.Equal(dbSize, new FileInfo(result.QuarantinedDatabasePath).Length);

        Assert.Contains(result.QuarantinedDatabasePath, result.MovedFiles);
        Assert.Empty(result.SkippedFiles);
    }

    [Fact]
    public void QuarantineCorruptDatabase_AllowsFreshInitializeAtSamePath_WithDifferentPassword()
    {
        // B50: after quarantine the original path is free, so the
        // user can initialize a fresh catalog with a brand new
        // password. The quarantined bytes must remain undisturbed by
        // the fresh initialize.
        using (var backend = new InMemorySnapshotBackend())
        {
            backend.Initialize(_dbPath, "OriginalPassword123!".AsSpan());
        }

        var quarantine = LocalDatabaseService.QuarantineCorruptDatabase(_dbPath);
        var quarantinedDbBytes = File.ReadAllBytes(quarantine.QuarantinedDatabasePath);

        using (var fresh = new InMemorySnapshotBackend())
        {
            fresh.Initialize(_dbPath, "DifferentFreshPassword456!".AsSpan());
            // Sanity: a fresh catalog has zero backed-up files.
            Assert.Empty(fresh.GetAllBackedUpFiles());
        }

        // Quarantined file is unchanged after the fresh initialize.
        Assert.True(File.Exists(quarantine.QuarantinedDatabasePath));
        Assert.Equal(quarantinedDbBytes, File.ReadAllBytes(quarantine.QuarantinedDatabasePath));
    }

    [Fact]
    public void QuarantineCorruptDatabase_OnMissingFile_ThrowsFileNotFoundException()
    {
        // B50: quarantining a non-existent file is a programming
        // error. We surface it as FileNotFoundException so the caller
        // sees the offending path in the exception message rather
        // than silently no-op'ing.
        var missing = Path.Combine(_testDir, "does-not-exist.db");
        var ex = Assert.Throws<FileNotFoundException>(
            () => LocalDatabaseService.QuarantineCorruptDatabase(missing));
        Assert.Equal(missing, ex.FileName);
    }

    [Fact]
    public void ReadPasswordSaltFromQuarantinedSnapshot_WithCorrectPassword_ReturnsStoredSalt()
    {
        // The rebuild-from-quarantined-catalog flow needs exactly one field
        // from the dead catalog -- the in-database PasswordSalt that the Azure
        // encryption key was derived from. This seeds a snapshot catalog with a
        // known PasswordSalt, quarantines it via the same code path the Settings
        // UI uses, and proves the reader recovers that salt by decrypting the
        // quarantined AZDB snapshot.
        const string password = "Quarantine-B51-Password!";
        var seededSalt = new byte[16];
        for (int i = 0; i < seededSalt.Length; i++) seededSalt[i] = (byte)(i + 1);

        using (var backend = new InMemorySnapshotBackend())
        {
            backend.Initialize(_dbPath, password.AsSpan());
            backend.SaveConfiguration(new BackupConfiguration
            {
                ContainerName = "rebuild-test",
                PasswordSalt = seededSalt,
            });
            backend.Checkpoint();
        }

        var quarantine = LocalDatabaseService.QuarantineCorruptDatabase(_dbPath);

        var recoveredSalt = InMemorySnapshotBackend.ReadPasswordSaltFromQuarantinedSnapshot(
            quarantine.QuarantinedDatabasePath,
            password.AsSpan());

        Assert.NotNull(recoveredSalt);
        Assert.Equal(seededSalt, recoveredSalt);
    }

    [Fact]
    public void ReadPasswordSaltFromQuarantinedSnapshot_WithWrongPassword_ThrowsInvalidPasswordException()
    {
        // The reader must NOT silently return garbage when the password is
        // wrong. The AES-256-GCM authentication tag is the single oracle for
        // password correctness; a wrong password fails the tag and surfaces as
        // InvalidPasswordException rather than letting a partial decrypt through.
        const string realPassword = "Correct-B51-Password!";
        const string wrongPassword = "Wrong-B51-Password!";

        using (var backend = new InMemorySnapshotBackend())
        {
            backend.Initialize(_dbPath, realPassword.AsSpan());
            backend.SaveConfiguration(new BackupConfiguration
            {
                PasswordSalt = new byte[16],
            });
            backend.Checkpoint();
        }

        var quarantine = LocalDatabaseService.QuarantineCorruptDatabase(_dbPath);

        Assert.Throws<InvalidPasswordException>(() =>
            InMemorySnapshotBackend.ReadPasswordSaltFromQuarantinedSnapshot(
                quarantine.QuarantinedDatabasePath,
                wrongPassword.AsSpan()));
    }

    [Fact]
    public void ReadPasswordSaltFromQuarantinedSnapshot_WithMissingFile_ThrowsFileNotFoundException()
    {
        // Callers should see the offending path, not a vague crypto error,
        // when the chosen snapshot file does not exist.
        var missing = Path.Combine(_testDir, "does-not-exist.db.quarantine-19990101-000000");

        var ex = Assert.Throws<FileNotFoundException>(() =>
            InMemorySnapshotBackend.ReadPasswordSaltFromQuarantinedSnapshot(
                missing,
                "AnyPassword123!".AsSpan()));
        Assert.Equal(missing, ex.FileName);
    }

    [Fact]
    public void ReadPasswordSaltFromQuarantinedSnapshot_WithNonSnapshotFile_ThrowsDbSnapshotException()
    {
        // A file that is not an AZDB snapshot (e.g. a stray non-catalog file the
        // user picked by mistake) must fail loudly with a snapshot error rather
        // than be misread.
        var bogus = Path.Combine(_testDir, "not-a-snapshot.db.quarantine-19990101-000000");
        File.WriteAllBytes(bogus, "this is not an AZDB snapshot"u8.ToArray());

        Assert.Throws<DbSnapshotException>(() =>
            InMemorySnapshotBackend.ReadPasswordSaltFromQuarantinedSnapshot(
                bogus,
                "AnyPassword123!".AsSpan()));
    }
}
