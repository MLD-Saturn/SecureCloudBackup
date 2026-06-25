using System.Diagnostics;
using SecureCloudBackup.Core.Models;
using SecureCloudBackup.Core.Services.Backends;

namespace SecureCloudBackup.Core.Services;

/// <summary>
/// Manages local database for tracking backup state, configuration, and file metadata.
/// Backed by a SQLCipher-encrypted SQLite database created via
/// <see cref="DatabaseBackendFactory.CreateAndInitializeSqlite"/>; every public
/// method on this class delegates to the underlying <see cref="SqliteBackend"/>
/// stored in the <c>_sqliteBackend</c> field. The SQLCipher KDF (Argon2id with
/// the parameters defined in <see cref="EncryptionService"/>) lives inside the
/// backend; this class is a thin facade and owns no encryption-related state.
/// </summary>
public partial class LocalDatabaseService : IDisposable
{
    /// <summary>
    /// Populated by <see cref="Initialize(string, ReadOnlySpan{char})"/>.
    /// Non-null after a successful Initialize. The <c>?</c> annotation
    /// only models the pre-Initialize and post-<see cref="Close"/> /
    /// <see cref="Dispose"/> states; every public method uses
    /// <see cref="GetBackend"/> to enforce the non-null invariant.
    /// </summary>
    private SqliteBackend? _sqliteBackend;

    private bool _disposed;
    private string? _databasePath;

    public bool IsInitialized => _sqliteBackend?.IsInitialized ?? false;

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
    private static string GetSaltFilePath(string databasePath) => CatalogPaths.GetSaltFilePath(databasePath);

    /// <summary>
    /// Returns the live <see cref="SqliteBackend"/> or throws if Initialize
    /// has not been called (or if Close / Dispose has cleared it). Replaces
    /// the pre-B60 EnsureInitialized helper that probed the LiteDB field.
    /// </summary>
    private SqliteBackend GetBackend()
    {
        var backend = _sqliteBackend
            ?? throw new InvalidOperationException(
                "Database not initialized. Call Initialize first.");
        return backend;
    }

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
    /// </summary>
    public void Close()
    {
        if (_sqliteBackend == null) return;
        _sqliteBackend.Close();
        _sqliteBackend = null;
        _databasePath = null;
    }

    #region Configuration

    /// <summary>
    /// Gets or creates the backup configuration.
    /// </summary>
    public BackupConfiguration GetConfiguration() => GetBackend().GetConfiguration();

    /// <summary>
    /// Saves the backup configuration using a transaction.
    /// </summary>
    public void SaveConfiguration(BackupConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        GetBackend().SaveConfiguration(config);
    }

    #endregion

    #region Backed Up Files

    /// <summary>
    /// Gets a backed up file by its local path.
    /// </summary>
    public BackedUpFile? GetBackedUpFile(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        return GetBackend().GetBackedUpFile(localPath);
    }

    /// <summary>
    /// Saves or updates a backed up file record using a transaction.
    /// </summary>
    public void SaveBackedUpFile(BackedUpFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        GetBackend().SaveBackedUpFile(file);
    }

    /// <summary>
    /// Gets all backed up files.
    /// </summary>
    public List<BackedUpFile> GetAllBackedUpFiles() => GetBackend().GetAllBackedUpFiles();

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
        GetBackend().BulkInsertBackedUpFiles(files);
    }

    /// <summary>
    /// B46: removes every backed-up-file row (and the cascaded
    /// <c>file_chunks</c> and <c>chunk_file_refs</c> rows) so the
    /// "Rebuild from Azure" recovery path can repopulate the catalog
    /// from a known-empty Files set rather than upserting on top of
    /// stale rows that may reference paths that no longer exist on
    /// disk.
    /// </summary>
    public void ClearBackedUpFiles() => GetBackend().ClearBackedUpFiles();

    #endregion

    #region Pending Changes Queue

    /// <summary>
    /// Adds a file change to the pending queue.
    /// </summary>
    public void QueueFileChange(FileChangeEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);
        GetBackend().QueueFileChange(change);
    }

    /// <summary>
    /// Bulk variant of <see cref="QueueFileChange"/>. All inserts run inside a single
    /// backend transaction, avoiding the per-change journal write. Preserves the
    /// "replace existing" semantics of the single-change variant by collecting
    /// affected paths first and issuing one delete-batch before the bulk insert.
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
        ArgumentNullException.ThrowIfNull(changes);
        GetBackend().QueueFileChangesBatch(changes);
    }

    /// <summary>
    /// Gets the next batch of pending changes.
    /// </summary>
    public List<FileChangeEvent> GetPendingChanges(int batchSize = 100)
        => GetBackend().GetPendingChanges(batchSize);

    /// <summary>
    /// Removes a pending change after it's been processed.
    /// </summary>
    public void RemovePendingChange(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        GetBackend().RemovePendingChange(filePath);
    }

    /// <summary>
    /// Gets all pending change file paths as a set for fast lookups.
    /// Use this instead of per-file IsFileChangePending calls when checking many files.
    /// </summary>
    public HashSet<string> GetAllPendingChangePaths() => GetBackend().GetAllPendingChangePaths();

    /// <summary>
    /// Removes pending changes for files that are already backed up with current content.
    /// This cleans up stale entries that may have been left behind.
    /// </summary>
    /// <remarks>
    /// B17: paginates through pending changes in fixed-size pages (10k rows)
    /// instead of asking for int.MaxValue in one call. Pre-B17 the int.MaxValue
    /// request reached SqliteBackend.GetPendingChanges which pre-allocated a
    /// List with that capacity, allocating ~51 GB of references and OOM-ing the
    /// unlock flow. Pagination keeps the per-call memory bounded AND lets a
    /// future cancel/abort interrupt the cleanup mid-stream.
    /// </remarks>
    public int CleanupStalePendingChanges()
    {
        var backend = GetBackend();
        var removedCount = 0;
        const int PageSize = 10_000;
        // Loop until a page comes back smaller than PageSize.
        // RemovePendingChange deletes the row in-place so on the next
        // iteration the SQL ORDER BY skips it; no offset arithmetic needed.
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

            // If we got a partial page AND removed nothing, we are looking at
            // rows we cannot clean up (file in use, etc.). Stop to avoid an
            // infinite loop re-reading the same un-removable rows on every
            // iteration.
            if (pending.Count < PageSize && removedThisPage == 0) break;
            // If we got a full page but removed nothing, we still need to
            // advance past these rows. The next iteration would re-fetch the
            // same page. Bail out to avoid the infinite loop; the un-removable
            // rows can wait until the next launch.
            if (removedThisPage == 0) break;
        }

        return removedCount;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _sqliteBackend?.Dispose();
        _sqliteBackend = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    #region Integrity Check Persistence (D1)

    // Forwarders to SqliteBackend. The integrity-check feature lives only on
    // the SQLite backend.

    public int InsertIntegrityCheckRun(IntegrityCheckRun run)
        => GetBackend().InsertIntegrityCheckRun(run);

    public void UpdateIntegrityCheckRun(IntegrityCheckRun run)
        => GetBackend().UpdateIntegrityCheckRun(run);

    public void InsertIntegrityCheckFailure(IntegrityCheckFailure failure)
        => GetBackend().InsertIntegrityCheckFailure(failure);

    public List<IntegrityCheckRun> GetRecentIntegrityCheckRuns(int limit)
        => _sqliteBackend?.GetRecentIntegrityCheckRuns(limit) ?? [];

    public List<IntegrityCheckFailure> GetIntegrityCheckFailures(int runId)
        => _sqliteBackend?.GetIntegrityCheckFailures(runId) ?? [];

    public void DeleteIntegrityCheckFailuresExcept(int keepRunId)
        => _sqliteBackend?.DeleteIntegrityCheckFailuresExcept(keepRunId);

    public void PruneIntegrityCheckRuns(int keep)
        => _sqliteBackend?.PruneIntegrityCheckRuns(keep);

    /// <summary>
    /// True when the integrity-check feature is available (i.e., the
    /// database has been initialised). Always true on a live service
    /// post-W4 Phase 3; remains a property so the UI binding survives
    /// the Initialize-not-yet-called window.
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
    public DatabaseFileIntegrityResult CheckDatabaseFileIntegrity()
        => GetBackend().CheckDatabaseFileIntegrity();

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
    public DatabaseRepairResult RepairDatabaseIndexes(DatabaseFileIntegrityResult diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        return GetBackend().ReindexCorruptIndexes(diagnosis);
    }

    /// <summary>
    /// D6: persists the upload-time encrypted-blob MD5 for a chunk so
    /// the cheap T1 integrity tier can compare against the live
    /// Azure-side ContentHash.
    /// </summary>
    public void SetChunkExpectedMd5(string chunkHash, byte[] md5)
        => GetBackend().SetChunkExpectedMd5(chunkHash, md5);

    /// <summary>
    /// D6: returns the persisted MD5 for a chunk, or null if the chunk
    /// was uploaded before D6 (the column is null until first observation).
    /// </summary>
    public byte[]? GetChunkExpectedMd5(string chunkHash)
        => GetBackend().GetChunkExpectedMd5(chunkHash);

    /// <summary>
    /// D10: enumerates chunk hashes whose <c>expected_encrypted_md5</c>
    /// column is null. The legacy-chunk backfill in
    /// <see cref="IntegrityCheckService"/> uses this to find chunks
    /// uploaded before D6.
    /// </summary>
    public IEnumerable<string> GetChunkHashesWithNullExpectedMd5()
        => GetBackend().GetChunkHashesWithNullExpectedMd5();

    /// <summary>
    /// D10: count of chunks awaiting MD5 backfill. The Storage Health
    /// UI shows this number on the "Backfill legacy chunks" button so
    /// the user knows the scope before triggering a network scan.
    /// </summary>
    public long CountChunksWithNullExpectedMd5()
        => _sqliteBackend?.CountChunksWithNullExpectedMd5() ?? 0;

    #endregion
}
