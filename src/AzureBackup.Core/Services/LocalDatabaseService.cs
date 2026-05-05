using System.Diagnostics;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;
using LiteDB;

namespace AzureBackup.Core.Services;

/// <summary>
/// Manages local database for tracking backup state, configuration, and file metadata.
/// Backed by a SQLCipher-encrypted SQLite database created via
/// <see cref="DatabaseBackendFactory.CreateAndInitializeSqlite"/>; every public
/// method on this class delegates to the underlying <see cref="SqliteBackend"/>
/// stored in the <c>_sqliteBackend</c> field.
///
/// <para>
/// W4 Phase 3 (B59) removed the historical LiteDB code path and the
/// AsyncLocal test override that pinned it; the LiteDB-typed collection
/// fields below remain only as the still-pending Phase-3-Commit-2 cleanup
/// surface that <see cref="Backends.LiteDbBackend"/> and the contract tests
/// reach through this class.
/// </para>
/// </summary>
public partial class LocalDatabaseService : IDisposable
{
    private LiteDatabase? _database;
    private ILiteCollection<BackupConfiguration>? _configCollection;
    private ILiteCollection<BackedUpFile>? _filesCollection;
    private ILiteCollection<FileChangeEvent>? _pendingChangesCollection;
    private ILiteCollection<ChunkIndexEntry>? _chunkIndexCollection;
    private ILiteCollection<IndexMetadata>? _indexMetadataCollection;
    private ILiteCollection<ChunkFileRefRow>? _chunkFileRefsCollection;

    /// <summary>
    /// Populated by <see cref="Initialize(string, ReadOnlySpan{char})"/>.
    /// Always non-null after a successful Initialize. The remaining
    /// LiteDB-typed fields above will be retired in W4 Phase 3 Commit 2;
    /// while they exist, every public method on this class checks
    /// <c>_sqliteBackend</c> first and delegates to it.
    /// </summary>
    private SqliteBackend? _sqliteBackend;

    /// <summary>
    /// Reader/writer lock guarding every access to the LiteDB collections.
    /// Readers proceed in parallel; writers are exclusive. Replaces the
    /// previous coarse <c>object</c> monitor so that read-heavy paths
    /// (statistics, chunk-index summary, orphan scan) no longer serialize
    /// behind each other or behind the backup loop.
    /// <para>
    /// <b>Recursion policy:</b> <see cref="LockRecursionPolicy.NoRecursion"/>.
    /// No public method acquires this lock and then calls another public
    /// method on the same instance; allowing recursion hides accidental
    /// nesting that would deadlock under upgradeable-read patterns.
    /// </para>
    /// </summary>
    private readonly ReaderWriterLockSlim _dbLock = new(LockRecursionPolicy.NoRecursion);

    /// <summary>
    /// Interval between automatic WAL checkpoints. LiteDB would otherwise only
    /// checkpoint at shutdown or when the <c>-log</c> file crosses its internal
    /// threshold, which this long-running app rarely reaches because its writes
    /// are small and sustained. A 1-hour cadence keeps the <c>-log</c> bounded
    /// without competing with short-lived transactional work.
    /// </summary>
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// <summary>
    /// Timer that invokes <see cref="Checkpoint"/> on the <see cref="CheckpointInterval"/>.
    /// Started in <see cref="Initialize(string, ReadOnlySpan{char})"/> and disposed
    /// in <see cref="Dispose"/>.
    /// </summary>
    private System.Threading.Timer? _checkpointTimer;

    private bool _disposed;
    private string? _databasePath;

    public bool IsInitialized => _sqliteBackend?.IsInitialized ?? (_database != null);

    /// <summary>
    /// Gets the current database file path.
    /// </summary>
    public string? DatabasePath => _sqliteBackend?.DatabasePath ?? _databasePath;

    /// <summary>
    /// Event for detailed debug/diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;

    [Conditional("DIAGNOSTICLOG")]
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [Database] {message}");
    }

    /// <summary>
    /// Gets the path to the database salt file.
    /// </summary>
    private static string GetSaltFilePath(string databasePath) => databasePath + ".salt";

    /// <summary>
    /// Initializes the database at the specified path with password encryption.
    /// The underlying SQLCipher backend derives a strong key from the password
    /// using Argon2id, providing protection against brute force attacks even
    /// if the database file is stolen.
    /// </summary>
    /// <param name="databasePath">Path to the database file</param>
    /// <param name="password">Password used to encrypt the database. Supplied as a span so the
    /// caller can keep the plaintext in a <c>char[]</c> and zero it after use.</param>
    /// <exception cref="InvalidPasswordException">Thrown if password is incorrect for existing database</exception>
    public void Initialize(string databasePath, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        // Defence-in-depth: detect re-entrant Initialize on the SAME
        // instance. Production code never re-enters Initialize on a
        // service that is already initializing; if a future caller
        // does, this guard surfaces the bug as a clear exception
        // instead of letting the stack blow up.
        if (_initializeInProgress)
        {
            throw new InvalidOperationException(
                "LocalDatabaseService.Initialize called re-entrantly on the same instance. " +
                "This indicates a code path that re-enters Initialize on a service that is " +
                "already initializing.");
        }

        _initializeInProgress = true;
        try
        {
            InitializeCore(databasePath, password);
        }
        finally
        {
            _initializeInProgress = false;
        }
    }

    /// <summary>
    /// Per-instance re-entry guard for <see cref="Initialize"/>. See
    /// the guard comment in Initialize for why this exists.
    /// </summary>
    private bool _initializeInProgress;

    private void InitializeCore(string databasePath, ReadOnlySpan<char> password)
    {
        // W4 Phase 3 (B59): SQLite is the only backend. Every public
        // method on this class checks _sqliteBackend at its top and
        // delegates to it; the legacy LiteDB code path was removed
        // along with DatabaseBackendFactory.ShouldUseSqlite and the
        // AsyncLocal test override.
        Log($"Initialize: routing to SqliteBackend");
        _databasePath = databasePath;
        // B13: forward the backend's diagnostic events through this
        // service's own DiagnosticLog so the file logger captures the
        // Argon2id KDF entry/exit/OOM messages.
        _sqliteBackend = DatabaseBackendFactory.CreateAndInitializeSqlite(
            databasePath, password,
            diagnosticLogSink: (s, msg) => DiagnosticLog?.Invoke(this, msg));
    }

    /// <summary>
    /// Legacy <c>string</c> overload of <see cref="Initialize(string, ReadOnlySpan{char})"/>.
    /// Prefer the span overload so the plaintext password does not linger on the managed heap.
    /// </summary>
    public void Initialize(string databasePath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Initialize(databasePath, password.AsSpan());
    }

    /// <summary>
    /// B50: closes the current database connection (if open) and moves
    /// the catalog file plus its companion <c>-wal</c>, <c>-shm</c>,
    /// <c>-journal</c>, and salt artefacts aside under a timestamped
    /// <c>.quarantine-yyyyMMdd-HHmmss</c> suffix. The next call to
    /// <see cref="Initialize(string, ReadOnlySpan{char})"/> against
    /// the same path will create a fresh database with a fresh salt.
    /// </summary>
    /// <remarks>
    /// Use this when the catalog cannot be opened or read (e.g.
    /// <c>SQLITE_CORRUPT</c> on every page) and the user needs a fresh
    /// catalog to keep working. The original bytes are preserved on
    /// disk for forensic analysis -- they are NOT securely deleted,
    /// because the user explicitly chose recovery over destruction.
    /// </remarks>
    /// <param name="databasePath">
    /// Path to the catalog database file to quarantine. Must match
    /// the path the open connection was created against; passing a
    /// different path is a programming error and throws.
    /// </param>
    /// <returns>
    /// Result describing the quarantined main database path, every
    /// companion file moved, and any companion file that could not
    /// be moved (typically locked by another process).
    /// </returns>
    public QuarantineResult QuarantineAndClose(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        // Close any live connection BEFORE moving the file. SQLite
        // holds an exclusive file handle on Windows; File.Move would
        // fail until the handle is released.
        Close();

        return QuarantineCorruptDatabase(databasePath);
    }

    /// <summary>
    /// Closes the current database connection.
    /// Used when migrating to allow reopening with different settings.
    /// </summary>
    public void Close()
    {
        if (_sqliteBackend != null)
        {
            _sqliteBackend.Close();
            _sqliteBackend = null;
            _databasePath = null;
            return;
        }

        // Stop the checkpoint timer first so it cannot fire against a disposed DB.
        _checkpointTimer?.Dispose();
        _checkpointTimer = null;

        InWriteLock(() =>
        {
            _database?.Dispose();
            _database = null;
            _configCollection = null;
            _filesCollection = null;
            _pendingChangesCollection = null;
            _chunkIndexCollection = null;
            _indexMetadataCollection = null;
            _chunkFileRefsCollection = null;
            Log("Close: Database connection closed");
        });
    }

    #region Configuration

    /// <summary>
    /// Gets or creates the backup configuration.
    /// </summary>
    public BackupConfiguration GetConfiguration()
    {
        if (_sqliteBackend != null) return _sqliteBackend.GetConfiguration();
        EnsureInitialized();

        // First attempt: take a read lock.
        // the upgrade entirely (the common case after first-run).
        var existing = InReadLock(() => _configCollection!.FindById(1));
        if (existing != null) return existing;

        // Row missing - promote to a write lock and insert the default.
        return InWriteLock(() =>
        {
            // Re-check under the write lock: another writer may have inserted
            // the row between our read and write.
            var config = _configCollection!.FindById(1);
            if (config != null) return config;

            config = new BackupConfiguration { Id = 1 };
            _configCollection.Insert(config);
            return config;
        });
    }

    /// <summary>
    /// Saves the backup configuration using a transaction.
    /// </summary>
    public void SaveConfiguration(BackupConfiguration config)
    {
        if (_sqliteBackend != null) { _sqliteBackend.SaveConfiguration(config); return; }
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(config);

        InWriteLock(() =>
        {
            _database!.BeginTrans();
            try
            {
                config.Id = 1;
                _configCollection!.Upsert(config);
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        });
    }

    #endregion

    #region Backed Up Files

    /// <summary>
    /// Gets a backed up file by its local path.
    /// </summary>
    public BackedUpFile? GetBackedUpFile(string localPath)
    {
        if (_sqliteBackend != null) return _sqliteBackend.GetBackedUpFile(localPath);
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        return InReadLock(() => _filesCollection!.FindOne(x => x.LocalPath == localPath));
    }

    /// <summary>
    /// Saves or updates a backed up file record using a transaction.
    /// </summary>
    public void SaveBackedUpFile(BackedUpFile file)
    {
        if (_sqliteBackend != null) { _sqliteBackend.SaveBackedUpFile(file); return; }
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(file);

        InWriteLock(() =>
        {
            _database!.BeginTrans();
            try
            {
                var existing = _filesCollection!.FindOne(x => x.LocalPath == file.LocalPath);
                if (existing != null)
                {
                    file.Id = existing.Id;
                    _filesCollection.Update(file);
                }
                else
                {
                    _filesCollection.Insert(file);
                }
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        });
    }

    /// <summary>
    /// Gets all backed up files.
    /// </summary>
    public List<BackedUpFile> GetAllBackedUpFiles()
    {
        if (_sqliteBackend != null) return _sqliteBackend.GetAllBackedUpFiles();
        EnsureInitialized();

        return InReadLock(() => _filesCollection!.FindAll().ToList());
    }

    /// <summary>
    /// B46: bulk-inserts <see cref="BackedUpFile"/> rows (with their full
    /// chunk lists) used by the "Rebuild from Azure" recovery path to
    /// repopulate the catalog from authoritative Azure metadata. The
    /// caller MUST clear the catalog first via
    /// <see cref="ClearBackedUpFiles"/>; this method assumes a clean
    /// Files set and uses plain inserts so a UNIQUE clash on
    /// <c>local_path</c> indicates a programmer error.
    /// </summary>
    public void BulkInsertBackedUpFiles(IEnumerable<BackedUpFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (_sqliteBackend != null) { _sqliteBackend.BulkInsertBackedUpFiles(files); return; }
        EnsureInitialized();

        InWriteLock(() =>
        {
            _database!.BeginTrans();
            try
            {
                _filesCollection!.InsertBulk(files);
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        });
    }

    /// <summary>
    /// B46: removes every backed-up-file row (and on SQLite the
    /// cascaded <c>file_chunks</c> and <c>chunk_file_refs</c> rows) so
    /// the "Rebuild from Azure" recovery path can repopulate the
    /// catalog from a known-empty Files set rather than upserting on
    /// top of stale rows that may reference paths that no longer exist
    /// on disk.
    /// </summary>
    public void ClearBackedUpFiles()
    {
        if (_sqliteBackend != null) { _sqliteBackend.ClearBackedUpFiles(); return; }
        EnsureInitialized();

        InWriteLock(() =>
        {
            _database!.BeginTrans();
            try
            {
                _filesCollection!.DeleteAll();
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        });
    }

    #endregion

    #region Pending Changes Queue

    /// <summary>
    /// Adds a file change to the pending queue.
    /// </summary>
    public void QueueFileChange(FileChangeEvent change)
    {
        if (_sqliteBackend != null) { _sqliteBackend.QueueFileChange(change); return; }
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(change);

        InWriteLock(() =>
        {
            _database!.BeginTrans();
            try
            {
                // Remove any existing pending change for the same file
                _pendingChangesCollection!.DeleteMany(x => x.FilePath == change.FilePath);
                _pendingChangesCollection.Insert(change);
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        });
    }

    /// <summary>
    /// Bulk variant of <see cref="QueueFileChange"/>. All inserts run inside a single
    /// LiteDB transaction, avoiding the per-change acquire/release of <c>_dbLock</c>
    /// and the per-change journal write. Preserves the "replace existing" semantics
    /// of the single-change variant by collecting affected paths first and issuing
    /// one <c>DeleteMany</c> before the bulk insert.
    /// </summary>
    /// <remarks>
    /// At ~10k changes (e.g. IDE rebuild or git checkout) this turns ~10k small
    /// transactions into a single one, cutting total commit time by 1-2 orders of
    /// magnitude and eliminating contention with the backup loop that reads from
    /// the same collection.
    /// </remarks>
    /// <param name="changes">
    /// The changes to persist. An empty or null sequence is a no-op. If multiple
    /// entries in the sequence share a <see cref="FileChangeEvent.FilePath"/> the
    /// last one wins - matching the semantics of repeated single-change calls.
    /// </param>
    public void QueueFileChangesBatch(IEnumerable<FileChangeEvent> changes)
    {
        if (_sqliteBackend != null) { _sqliteBackend.QueueFileChangesBatch(changes); return; }
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(changes);

        // Materialise once
        // issue a single DeleteMany covering every affected path before the insert.
        var byPath = new Dictionary<string, FileChangeEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (change is null) continue;
            byPath[change.FilePath] = change;
        }

        if (byPath.Count == 0) return;

        InWriteLock(() =>
        {
            _database!.BeginTrans();
            try
            {
                // Remove existing pending rows for every affected path, then bulk-insert.
                // The DeleteMany predicate on LiteDB compiles to a BSON query, so we pass
                // a simple equality check per path rather than relying on HashSet.Contains
                // which LiteDB's expression visitor does not support.
                foreach (var path in byPath.Keys)
                {
                    _pendingChangesCollection!.DeleteMany(x => x.FilePath == path);
                }

                // InsertBulk is the LiteDB batch-insert primitive.
                _pendingChangesCollection!.InsertBulk(byPath.Values);
                _database.Commit();
            }
            catch
            {
                _database.Rollback();
                throw;
            }
        });
    }

    /// <summary>
    /// Gets the next batch of pending changes.
    /// </summary>
    public List<FileChangeEvent> GetPendingChanges(int batchSize = 100)
    {
        if (_sqliteBackend != null) return _sqliteBackend.GetPendingChanges(batchSize);
        EnsureInitialized();
        if (batchSize <= 0) batchSize = 100;

        return InReadLock(() =>
            _pendingChangesCollection!
                .FindAll()
                .OrderBy(x => x.DetectedAt)
                .Take(batchSize)
                .ToList());
    }

    /// <summary>
    /// Removes a pending change after it's been processed.
    /// </summary>
    public void RemovePendingChange(string filePath)
    {
        if (_sqliteBackend != null) { _sqliteBackend.RemovePendingChange(filePath); return; }
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        InWriteLock(() => _pendingChangesCollection!.DeleteMany(x => x.FilePath == filePath));
    }

    /// <summary>
    /// Gets all pending change file paths as a set for fast lookups.
    /// Use this instead of per-file IsFileChangePending calls when checking many files.
    /// </summary>
    public HashSet<string> GetAllPendingChangePaths()
    {
        if (_sqliteBackend != null) return _sqliteBackend.GetAllPendingChangePaths();
        EnsureInitialized();

        return InReadLock(() =>
            _pendingChangesCollection!
                .Query()
                .Select(x => x.FilePath)
                .ToEnumerable()
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Removes pending changes for files that are already backed up with current content.
    /// This cleans up stale entries that may have been left behind.
    /// </summary>
    public int CleanupStalePendingChanges()
    {
        if (_sqliteBackend != null)
        {
            // Rewrite of the LiteDB version below using ONLY backend
            // primitives. Same algorithm: for each pending change, look
            // up the file row; if the file is completed and on disk and
            // size-matches, drop the pending row. If the change is a
            // delete and the file is excluded, drop the pending row.
            //
            // B17: paginate through pending changes in fixed-size pages
            // (10k rows) instead of asking for int.MaxValue in one call.
            // Pre-B17 the int.MaxValue request reached SqliteBackend
            // .GetPendingChanges which pre-allocated a List<T> with that
            // capacity, allocating ~51 GB of references and OOM-ing the
            // unlock flow. Real bug observed by tester: the unlock
            // succeeded at the database level but failed during
            // RefreshStatistics with "Insufficient memory to continue
            // the execution of the program." (See B15 stack trace.)
            // Pagination keeps the per-call memory bounded AND lets a
            // future cancel/abort interrupt the cleanup mid-stream.
            var backend = _sqliteBackend;
            var removedCount = 0;
            const int PageSize = 10_000;
            // We loop until a page comes back smaller than PageSize.
            // RemovePendingChange deletes the row in-place so on the
            // next iteration the SQL ORDER BY skips it; no offset
            // arithmetic needed.
            while (true)
            {
                var pending = backend.GetPendingChanges(PageSize);
                if (pending.Count == 0) break;

                var removedThisPage = 0;
                foreach (var change in pending)
                {
                    var backedUp = backend.GetBackedUpFile(change.FilePath);
                    if (backedUp != null && backedUp.Status == BackupStatus.Completed)
                    {
                        try
                        {
                            System.IO.FileInfo fileInfo = new(change.FilePath);
                            if (fileInfo.Exists && fileInfo.Length == backedUp.FileSize)
                            {
                                backend.RemovePendingChange(change.FilePath);
                                removedCount++;
                                removedThisPage++;
                            }
                        }
                        catch
                        {
                            // Can't access file - leave in pending queue
                        }
                    }
                    else if (change.ChangeType == FileChangeType.Deleted)
                    {
                        if (backedUp != null && backedUp.Status == BackupStatus.Excluded)
                        {
                            backend.RemovePendingChange(change.FilePath);
                            removedCount++;
                            removedThisPage++;
                        }
                    }
                }

                // If we got a partial page AND removed nothing, we are
                // looking at rows we cannot clean up (file in use, etc.)
                // Stop to avoid an infinite loop re-reading the same
                // un-removable rows on every iteration.
                if (pending.Count < PageSize && removedThisPage == 0) break;
                // If we got a full page but removed nothing, we still
                // need to advance past these rows. The next iteration
                // would re-fetch the same page. Bail out to avoid the
                // infinite loop; the un-removable rows can wait until
                // the next launch.
                if (removedThisPage == 0) break;
            }

            return removedCount;
        }

        EnsureInitialized();

        return InWriteLock(() =>
        {
            var pendingChanges = _pendingChangesCollection!.FindAll().ToList();
            var removedCount = 0;

            foreach (var change in pendingChanges)
            {
                // Check if the file is already backed up
                var backedUp = _filesCollection!.FindOne(x => x.LocalPath == change.FilePath);
                if (backedUp != null && backedUp.Status == BackupStatus.Completed)
                {
                    // Check if the file still exists and matches the backup
                    try
                    {
                        System.IO.FileInfo fileInfo = new(change.FilePath);
                        if (fileInfo.Exists && fileInfo.Length == backedUp.FileSize)
                        {
                            // File is backed up and size matches - remove from pending
                            _pendingChangesCollection.DeleteMany(x => x.FilePath == change.FilePath);
                            removedCount++;
                        }
                    }
                    catch
                    {
                        // Can't access file - leave in pending queue
                    }
                }
                else if (change.ChangeType == FileChangeType.Deleted)
                {
                    // File was deleted and we've recorded it - remove from pending
                    if (backedUp != null && backedUp.Status == BackupStatus.Excluded)
                    {
                        _pendingChangesCollection.DeleteMany(x => x.FilePath == change.FilePath);
                        removedCount++;
                    }
                }
            }

            return removedCount;
        });
    }

    #endregion



    private void EnsureInitialized()
    {
        if (_database == null)
            throw new InvalidOperationException("Database not initialized. Call Initialize first.");
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_sqliteBackend != null)
        {
            _sqliteBackend.Dispose();
            _sqliteBackend = null;
            _disposed = true;
            GC.SuppressFinalize(this);
            return;
        }

        _checkpointTimer?.Dispose();
        _checkpointTimer = null;
        _database?.Dispose();
        _database = null;
        _dbLock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Timer callback that runs <see cref="Checkpoint"/>. Swallows exceptions so
    /// a transient checkpoint failure (e.g. during concurrent <see cref="Close"/>)
    /// does not take the timer thread down.
    /// </summary>
    private void CheckpointTimerCallback(object? state)
    {
        if (_disposed || _database == null) return;
        try
        {
            Checkpoint();
        }
        catch (Exception ex)
        {
            Log($"CheckpointTimerCallback: Checkpoint failed: {ex.Message}");
        }
    }

    #region Integrity Check Persistence (D1)

    // Forwarders to SqliteBackend. The integrity-check feature lives only on
    // the SQLite backend (production default since C-5/C-6); the LiteDB
    // legacy fallback is shrinking and acquiring a second schema there
    // would cost more than it earns. Calls on a LiteDB backend throw
    // NotSupportedException so the UI surfaces a clear "switch to SQLite"
    // diagnostic rather than silently corrupting.
    private const string IntegrityNotSupportedOnLiteDb =
        "Data integrity check requires the SQLite backend. The LiteDB legacy backend " +
        "does not store integrity-check history. Re-run after the next app launch " +
        "(C-5 migration is automatic) to enable this feature.";

    public int InsertIntegrityCheckRun(IntegrityCheckRun run)
    {
        if (_sqliteBackend != null) return _sqliteBackend.InsertIntegrityCheckRun(run);
        throw new NotSupportedException(IntegrityNotSupportedOnLiteDb);
    }

    public void UpdateIntegrityCheckRun(IntegrityCheckRun run)
    {
        if (_sqliteBackend != null) { _sqliteBackend.UpdateIntegrityCheckRun(run); return; }
        throw new NotSupportedException(IntegrityNotSupportedOnLiteDb);
    }

    public void InsertIntegrityCheckFailure(IntegrityCheckFailure failure)
    {
        if (_sqliteBackend != null) { _sqliteBackend.InsertIntegrityCheckFailure(failure); return; }
        throw new NotSupportedException(IntegrityNotSupportedOnLiteDb);
    }

    public List<IntegrityCheckRun> GetRecentIntegrityCheckRuns(int limit)
    {
        if (_sqliteBackend != null) return _sqliteBackend.GetRecentIntegrityCheckRuns(limit);
        return [];
    }

    public List<IntegrityCheckFailure> GetIntegrityCheckFailures(int runId)
    {
        if (_sqliteBackend != null) return _sqliteBackend.GetIntegrityCheckFailures(runId);
        return [];
    }

    public void DeleteIntegrityCheckFailuresExcept(int keepRunId)
    {
        if (_sqliteBackend != null) { _sqliteBackend.DeleteIntegrityCheckFailuresExcept(keepRunId); return; }
        // LiteDB no-op: nothing to delete because nothing was ever inserted.
    }

    public void PruneIntegrityCheckRuns(int keep)
    {
        if (_sqliteBackend != null) { _sqliteBackend.PruneIntegrityCheckRuns(keep); return; }
        // LiteDB no-op.
    }

    /// <summary>
    /// True when the integrity-check feature is available (i.e., the
    /// SQLite backend is in use). The UI greys out the "Check Data
    /// Integrity" button when this returns false.
    /// </summary>
    public bool IntegrityCheckSupported => _sqliteBackend != null;

    /// <summary>
    /// B44: runs SQLCipher's <c>cipher_integrity_check</c> and SQLite's
    /// <c>integrity_check</c> against the open database file. Returns
    /// the structured result so callers can present cipher-level vs
    /// SQLite-level diagnostics separately.
    ///
    /// <para>
    /// This verifies the on-disk database FILE itself, NOT the user's
    /// backed-up files. Use it when an operation against the local
    /// catalog fails with SQLite error 11
    /// (<c>SQLITE_CORRUPT</c>, "database disk image is malformed") to
    /// confirm whether the catalog file is the cause.
    /// </para>
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when the active backend is not SQLite (the LiteDB legacy
    /// backend has no equivalent pragma).
    /// </exception>
    public DatabaseFileIntegrityResult CheckDatabaseFileIntegrity()
    {
        if (_sqliteBackend != null) return _sqliteBackend.CheckDatabaseFileIntegrity();
        throw new NotSupportedException(
            "Database file integrity check requires the SQLite backend. " +
            "The LiteDB legacy backend does not expose the SQLCipher / SQLite " +
            "integrity-pragma surface.");
    }

    /// <summary>
    /// B45: attempts an in-place REINDEX-based repair of indexes that the
    /// supplied <paramref name="diagnosis"/> identified as REINDEX-safe
    /// damage. Returns a structured result indicating whether the repair
    /// was attempted, which indexes were rewritten, and the post-repair
    /// integrity-pragma output.
    ///
    /// <para>
    /// This is a NARROW repair: it only touches index pages and refuses
    /// to act on cipher-pragma damage, page-level damage, or any
    /// integrity_check finding the classifier doesn't recognise as
    /// safe. See <see cref="Backends.DatabaseRepairClassifier"/> for the
    /// whitelist.
    /// </para>
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when the active backend is not SQLite.
    /// </exception>
    public DatabaseRepairResult RepairDatabaseIndexes(DatabaseFileIntegrityResult diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        if (_sqliteBackend != null) return _sqliteBackend.ReindexCorruptIndexes(diagnosis);
        throw new NotSupportedException(
            "Database repair requires the SQLite backend. " +
            "The LiteDB legacy backend does not expose REINDEX.");
    }

    /// <summary>
    /// D6: persists the upload-time encrypted-blob MD5 for a chunk so
    /// the cheap T1 integrity tier can compare against the live
    /// Azure-side ContentHash.
    /// </summary>
    /// <remarks>
    /// SQLite is the only backend in production (W4 Phase 3 / B59).
    /// The throw guard is retained until the LiteDB-shaped contract
    /// tests are retired in Phase 3 Commit 2 because they still
    /// instantiate <see cref="LocalDatabaseService"/> through the
    /// <c>LiteDbBackend</c> wrapper, and a wrapper-only call site
    /// without a SQLite backend would otherwise silently lose MD5 data.
    /// </remarks>
    public void SetChunkExpectedMd5(string chunkHash, byte[] md5)
    {
        if (_sqliteBackend == null)
            throw new InvalidOperationException(
                "SetChunkExpectedMd5 requires the SQLite backend. SQLite is the production default; " +
                "if you are seeing this in production a launch-time migration was bypassed.");
        _sqliteBackend.SetChunkExpectedMd5(chunkHash, md5);
    }

    /// <summary>
    /// D6: returns the persisted MD5 for a chunk, or null if the chunk
    /// was uploaded before D6 (the column is null until first observation).
    /// </summary>
    public byte[]? GetChunkExpectedMd5(string chunkHash)
    {
        if (_sqliteBackend == null)
            throw new InvalidOperationException(
                "GetChunkExpectedMd5 requires the SQLite backend.");
        return _sqliteBackend.GetChunkExpectedMd5(chunkHash);
    }

    /// <summary>
    /// D10: enumerates chunk hashes whose <c>expected_encrypted_md5</c>
    /// column is null. The legacy-chunk backfill in
    /// <see cref="IntegrityCheckService"/> uses this to find chunks
    /// uploaded before D6.
    /// </summary>
    public IEnumerable<string> GetChunkHashesWithNullExpectedMd5()
    {
        if (_sqliteBackend == null)
            throw new InvalidOperationException("GetChunkHashesWithNullExpectedMd5 requires the SQLite backend.");
        return _sqliteBackend.GetChunkHashesWithNullExpectedMd5();
    }

    /// <summary>
    /// D10: count of chunks awaiting MD5 backfill. The Storage Health
    /// UI shows this number on the "Backfill legacy chunks" button so
    /// the user knows the scope before triggering a network scan.
    /// </summary>
    public long CountChunksWithNullExpectedMd5()
    {
        if (_sqliteBackend == null) return 0;
        return _sqliteBackend.CountChunksWithNullExpectedMd5();
    }

    #endregion
}
