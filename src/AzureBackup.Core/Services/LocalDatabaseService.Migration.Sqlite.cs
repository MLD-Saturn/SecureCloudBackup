using AzureBackup.Core.Models;
using AzureBackup.Core.Services.Backends;

namespace AzureBackup.Core.Services;

/// <summary>
/// Option C / C-2: migration from an on-disk LiteDB database to a
/// SQLCipher-encrypted SQLite database at the same path.
///
/// <para>
/// <b>When migration runs.</b> The <c>AZBK_USE_SQLITE</c> feature
/// flag from C-1 step b routes <see cref="Initialize"/> through
/// <c>SqliteBackend</c>. When the flag is on AND an existing database
/// file is found at the target path AND that file is NOT already a
/// SQLite database (the wrong-password probe in
/// <c>SqliteBackend.Initialize</c> threw), we assume it's a LiteDB
/// database, run the migration, and then open the fresh SQLite
/// database that migration produced.
/// </para>
///
/// <para>
/// <b>Atomicity.</b> Migration writes to a temp SQLite database at
/// <c>&lt;path&gt;.sqlite-tmp</c>. On successful copy + reverse-index
/// mark, we:
/// </para>
/// <list type="number">
///   <item>Close both databases (forces WAL checkpoint on SQLite).</item>
///   <item><see cref="File.Move"/> the original LiteDB at <c>&lt;path&gt;</c>
///     to <c>&lt;path&gt;.litedb-backup</c>. This FREES the target path.</item>
///   <item><see cref="File.Move"/> the original LiteDB salt at
///     <c>&lt;path&gt;.salt</c> to <c>&lt;path&gt;.litedb-backup.salt</c>.</item>
///   <item><see cref="File.Move"/> the temp SQLite at
///     <c>&lt;path&gt;.sqlite-tmp</c> to <c>&lt;path&gt;</c>.</item>
///   <item><see cref="File.Move"/> the temp SQLite salt at
///     <c>&lt;path&gt;.sqlite-tmp.salt</c> to <c>&lt;path&gt;.salt</c>.</item>
/// </list>
///
/// <para>
/// If ANY step from (3) onward fails the user ends in an inconsistent
/// state on disk (partial rename sequence). The <see cref="File.Move"/>
/// calls are fast OS-level rename operations on the same volume, so the
/// practical failure modes are (a) permissions (caller can fix and
/// retry) or (b) the file was locked by another process (rare; the
/// connections we own are already closed). In both cases the user can
/// manually rename the files into place - the logs say exactly what to
/// do.
/// </para>
///
/// <para>
/// <b>LiteDB backup retention.</b> The <c>.litedb-backup</c> file is
/// NOT deleted automatically. The user can manually delete it once
/// they're confident the SQLite database works. A future commit may
/// auto-delete after one successful app launch.
/// </para>
///
/// <para>
/// <b>Cancellation.</b> The per-table loops call
/// <see cref="CancellationToken.ThrowIfCancellationRequested"/> between
/// batches. If the user cancels mid-migration we delete the temp SQLite
/// file and leave the LiteDB database untouched. The next
/// Initialize call sees the LiteDB database, retries migration from
/// scratch.
/// </para>
/// </summary>
public partial class LocalDatabaseService
{
    /// <summary>
    /// Extension on the temp SQLite file written during migration.
    /// <c>&lt;originalPath&gt;.sqlite-tmp</c>. If you see this file on
    /// disk a previous migration was interrupted; the LiteDB database
    /// at the original path is still authoritative.
    /// </summary>
    internal const string SqliteMigrationTempSuffix = ".sqlite-tmp";

    /// <summary>
    /// Extension on the renamed-aside LiteDB database after a
    /// successful migration. <c>&lt;originalPath&gt;.litedb-backup</c>.
    /// The user can manually delete this file once confident in the
    /// SQLite database.
    /// </summary>
    internal const string LiteDbBackupSuffix = ".litedb-backup";

    /// <summary>
    /// Probes whether <paramref name="databasePath"/> can be opened as
    /// a SQLCipher-encrypted SQLite database with the given password.
    /// Returns true if successful. Returns false if the open fails with
    /// <see cref="InvalidPasswordException"/> (strong signal that the
    /// file is NOT a SQLite database in that password's encryption
    /// scheme - most likely a LiteDB file instead). Returns false if
    /// no file exists at the path.
    /// </summary>
    /// <remarks>
    /// Public so the UI layer can decide whether to show a migration
    /// progress modal BEFORE calling
    /// <see cref="Initialize(string, ReadOnlySpan{char})"/>.
    /// Any exception other than <see cref="InvalidPasswordException"/>
    /// propagates - the file is genuinely unreadable and migration
    /// would not help.
    /// </remarks>
    public static bool IsExistingSqliteDatabase(string databasePath, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));
        if (!File.Exists(databasePath)) return false;

        // Short-circuit BEFORE opening the file as SQLite if there is
        // no salt next door. The SqliteBackend.Initialize path would
        // otherwise call LoadOrCreateSalt and write a fresh random
        // salt to <path>.salt - a side-effect that competes with the
        // C-2 migration recovery code (a crash between rename steps
        // 2 and 3 leaves a SQLite DB at <path> with its salt at
        // <path>.sqlite-tmp.salt; if the probe runs first and writes
        // a random salt to <path>.salt the recovery completion would
        // overwrite that random salt but the side-effect window is
        // hostile to manual recovery).
        //
        // Returning false here is the correct answer: a SQLite DB
        // with no companion salt cannot be opened with the supplied
        // password; downstream migration code will detect either a
        // recoverable interrupted-migration state or a fresh
        // database situation.
        if (!File.Exists(databasePath + ".salt")) return false;

        try
        {
            using var probe = new SqliteBackend();
            probe.Initialize(databasePath, password);
            // Initialize succeeded => this IS a SQLite database with the
            // supplied password. Fall through to "true".
            return true;
        }
        catch (InvalidPasswordException)
        {
            // Wrong password for a SQLite DB, OR (more likely given the
            // callsite) not a SQLite DB at all. Either way the
            // appropriate next step is to try LiteDB.
            return false;
        }
    }

    /// <summary>
    /// Deletes any leftover <c>.litedb-backup</c> + <c>.litedb-backup.salt</c>
    /// files next to <paramref name="databasePath"/>. C-5 cleanup
    /// helper for users who migrated under the prior retention policy
    /// (which kept the LiteDB backup on disk indefinitely as a manual
    /// rollback artefact). Idempotent and silent when no backup exists.
    /// </summary>
    /// <remarks>
    /// Safe to call on every launch from the UI's auth flow because
    /// the only way a <c>.litedb-backup</c> exists at all is via this
    /// codebase's migration step 1; there is no third-party producer.
    /// On a fresh user (no prior migration) both files are absent and
    /// this method does nothing.
    /// </remarks>
    public static void CleanupStaleLegacyBackup(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var paths = new MigrationPaths(databasePath);
        FileSystemHelper.TrySecureDelete(paths.LiteDbBackup);
        FileSystemHelper.TrySecureDelete(paths.LiteDbBackupSalt);
    }

    /// <summary>
    /// Full LiteDB-to-SQLite migration. Caller guarantees that
    /// <paramref name="databasePath"/> points at a LiteDB database
    /// that opens successfully with <paramref name="password"/>.
    /// On success the method returns having:
    /// <list type="bullet">
    ///   <item>Written every row from the LiteDB database into a fresh
    ///     SQLite database at <paramref name="databasePath"/>.</item>
    ///   <item>Renamed the original LiteDB database to
    ///     <paramref name="databasePath"/> + <c>.litedb-backup</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="databasePath">Path to the LiteDB database that is the
    /// migration source; after success this path hosts a SQLite database.</param>
    /// <param name="password">Password for BOTH the source LiteDB
    /// (opened here) and the destination SQLite (created here). Matches
    /// production intent: the user supplies one password and migration
    /// preserves it.</param>
    /// <param name="progress">Optional progress reporter. The
    /// <c>total</c> component is an UPPER BOUND for the whole migration
    /// (files + chunks + pending changes); <c>processed</c> increments
    /// monotonically as each per-table phase completes.</param>
    /// <param name="cancellationToken">Cooperative cancellation. Checked
    /// between per-table phases. On cancellation the temp SQLite file is
    /// deleted and the LiteDB file is left untouched.</param>
    public static void MigrateFromLiteDb(
        string databasePath,
        ReadOnlySpan<char> password,
        IProgress<(int processed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        var paths = new MigrationPaths(databasePath);

        // C-2 crash-recovery: if a prior migration on this path was
        // interrupted (the .litedb-backup is present), reverse or
        // resume the rename dance BEFORE doing anything else. This
        // turns a previously-data-losing crash window between the
        // four File.Move calls into a transparent recovery that
        // either (a) restores the LiteDB database and retries
        // migration from scratch or (b) finishes the SQLite-side
        // commit. See TryRecoverFromInterruptedMigration for the
        // state-by-state decision table.
        var recovery = TryRecoverFromInterruptedMigration(paths);
        if (recovery == InterruptedMigrationOutcome.RecoveredAsSqlite)
        {
            // Step 3 was the only thing missing; we just renamed
            // tmpsalt into place. The target path is now a fully
            // formed SQLite DB. Caller should NOT proceed with
            // migration.
            return;
        }
        // RecoveredAsLiteDb and NoInterruption both fall through to
        // the regular migration code below.

        // Guard: refuse to migrate a non-existent source file. This
        // would otherwise cause LiteDB to silently CREATE an empty
        // database at databasePath (its constructor's default
        // behaviour) and we would migrate that empty database into
        // SQLite - which is exactly the data-loss path the recovery
        // logic above is designed to prevent. Belt-and-braces: even
        // with recovery, a user running MigrateFromLiteDb against a
        // fresh path with no LiteDB backup nearby gets a clean error
        // instead of a silent empty migration.
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                "Cannot migrate: no database file exists at the source path. " +
                "If a previous migration was interrupted, the LiteDB database " +
                "should be at <path>.litedb-backup; rename it back manually " +
                "before retrying.",
                databasePath);
        }

        var tempSqlitePath = paths.TempSqlite;
        var tempSqliteSaltPath = paths.TempSqliteSalt;
        var litedbBackupPath = paths.LiteDbBackup;
        var litedbBackupSaltPath = paths.LiteDbBackupSalt;
        var originalSaltPath = paths.OriginalSalt;

        // Defensive: if a prior migration left stale temp artefacts
        // behind, clear them before starting. Otherwise SqliteBackend
        // would open the partial file and misread its state.
        FileSystemHelper.TrySecureDelete(tempSqlitePath);
        FileSystemHelper.TrySecureDelete(tempSqlitePath + "-wal");
        FileSystemHelper.TrySecureDelete(tempSqlitePath + "-shm");
        FileSystemHelper.TrySecureDelete(tempSqliteSaltPath);

        // Use a temporary LocalDatabaseService that opens the file as
        // LiteDB regardless of feature-flag state. InitializeLiteDbOnly
        // calls InitializeLiteDbCore directly so we never bounce
        // through Initialize and never need to read the env var or
        // AsyncLocal override.
        using var liteDb = new LocalDatabaseService();
        liteDb.InitializeLiteDbOnly(databasePath, password);

        // Create the destination SQLite backend at the TEMP path so
        // a crash mid-copy leaves the original LiteDB authoritative.
        using var sqlite = new SqliteBackend();
        try
        {
            sqlite.Initialize(tempSqlitePath, password);

            // Phase 1: configuration (one row).
            cancellationToken.ThrowIfCancellationRequested();
            var config = liteDb.GetConfiguration();
            sqlite.SaveConfiguration(config);

            // Phase 2: chunk_index (may be 10K-500K rows at production scale).
            cancellationToken.ThrowIfCancellationRequested();
            var chunkIndex = liteDb.GetAllChunkIndexEntries();
            // BulkInsert path: internal to SQLiteBackend, single transaction,
            // ~1-2 s at 500K rows based on C-3 (2/N) numbers.
            sqlite.BulkInsertChunkIndexEntries(chunkIndex);

            // Phase 3: files + file_chunks + chunk_file_refs. BulkInsertFiles
            // writes all three tables in one transaction AND populates the
            // reverse index inline. At 5000 files x 100 chunks each this takes
            // ~2-4 s per C-3 (5b) measurements.
            cancellationToken.ThrowIfCancellationRequested();
            var allFiles = liteDb.GetAllBackedUpFiles();
            sqlite.BulkInsertFiles(allFiles);

            // Phase 4: pending_changes. Usually tiny (in-flight watcher
            // events) so a single bulk batch is sufficient.
            cancellationToken.ThrowIfCancellationRequested();
            var pending = liteDb.GetPendingChanges(int.MaxValue);
            if (pending.Count > 0)
            {
                sqlite.QueueFileChangesBatch(pending);
            }

            // Phase 5: index_metadata. Copy every (key, value) row. This
            // includes the ReverseIndexBuiltAt sentinel that BulkInsertFiles
            // has already effectively satisfied (the reverse index IS
            // populated inline), plus any app-specific keys like LastScan
            // that we do not statically know. If the source did not have
            // a ReverseIndexBuiltAt row we synthesise one now - the
            // migration itself fulfilled the contract that sentinel
            // represents, so post-migration code that asks
            // IsReverseChunkIndexBuilt() must see true.
            cancellationToken.ThrowIfCancellationRequested();
            var allMetadata = liteDb.GetAllIndexMetadata();
            foreach (var (key, value) in allMetadata)
            {
                sqlite.SetIndexMetadata(key, value);
            }
            if (!allMetadata.ContainsKey("ReverseIndexBuiltAt"))
            {
                sqlite.SetIndexMetadata("ReverseIndexBuiltAt", DateTime.UtcNow);
            }

            // Report pre-close progress so a UI hooked up to the callback
            // shows "100%" before the rename dance runs.
            var totalRows = chunkIndex.Count + allFiles.Count + pending.Count;
            progress?.Report((totalRows, totalRows));

            // Flush SQLite to disk before renaming. Close() forces a WAL
            // checkpoint(TRUNCATE) which leaves the -wal file empty.
            sqlite.Close();
            liteDb.Close();
        }
        catch
        {
            // Clean up the partial temp file before propagating.
            try { sqlite.Close(); } catch { /* best effort */ }
            try { liteDb.Close(); } catch { /* best effort */ }
            FileSystemHelper.TrySecureDelete(tempSqlitePath);
            FileSystemHelper.TrySecureDelete(tempSqlitePath + "-wal");
            FileSystemHelper.TrySecureDelete(tempSqlitePath + "-shm");
            FileSystemHelper.TrySecureDelete(tempSqliteSaltPath);
            throw;
        }

        // Atomic(ish) five-step rename dance. Each File.Move on the
        // same volume is atomic at the FS level; the only cross-step
        // hazard is a process crash or power loss BETWEEN moves. The
        // recovery code at the top of this method
        // (TryRecoverFromInterruptedMigration) detects each
        // intermediate state by inspecting the on-disk files and
        // either reverses (steps 1, 1b) or completes (steps 2, 3, 4)
        // the dance on the next launch. Crash states:
        //
        //   After step 1 (LiteDB DB moved out, salt still in place):
        //     Recovery reverses the move -> next attempt restarts.
        //   After step 1b (LiteDB DB AND salt moved out):
        //     Recovery reverses both moves -> next attempt restarts.
        //   After step 2 (SQLite DB at target, no SQLite salt yet):
        //     Recovery completes step 3 (move salt) and step 4 (delete
        //     backup) -> opens cleanly. Note that the next-launch probe
        //     must NOT auto-create a salt at this point - that is
        //     enforced by IsExistingSqliteDatabase skipping the probe
        //     when no .salt is present.
        //   After step 3 (SQLite DB + salt at target, backup still on disk):
        //     Recovery completes step 4 (delete backup) -> steady state.
        //   After step 4: fully migrated, no LiteDB residue.
        //
        // C-5: the .litedb-backup files are DELETED at step 4 (formerly
        // retained as a manual rollback artefact). Once SQLite is the
        // production default the manual rollback path is no longer
        // supported - if SQLite has a bug the user reports it and we
        // fix forward. Keeping the backup around indefinitely was a
        // disk-space tax with no compensating safety value.
        File.Move(databasePath, litedbBackupPath);
        if (File.Exists(originalSaltPath))
            File.Move(originalSaltPath, litedbBackupSaltPath);
        File.Move(tempSqlitePath, databasePath);
        File.Move(tempSqliteSaltPath, databasePath + ".salt");
        FileSystemHelper.TrySecureDelete(litedbBackupPath);
        FileSystemHelper.TrySecureDelete(litedbBackupSaltPath);
    }

    /// <summary>
    /// Private escape hatch used by <see cref="MigrateFromLiteDb"/>:
    /// opens the database as LiteDB regardless of any feature-flag
    /// state. Calls the extracted <c>InitializeLiteDbCore</c> directly
    /// so we never bounce through the public <c>Initialize</c> entry
    /// point and never have to mutate the env var or AsyncLocal
    /// override.
    /// </summary>
    /// <remarks>
    /// Earlier versions of this method did the env-var snapshot/clear/
    /// restore dance to force <c>Initialize</c> down the LiteDB
    /// branch. That broke after the C-2 AsyncLocal override was added
    /// because the override took precedence over the env var (commit
    /// <c>9fda662</c> shipped with infinite recursion as a result;
    /// <c>f319782</c> was the fix). This direct-call form is
    /// structurally immune: there is no flag to read.
    /// </remarks>
    private void InitializeLiteDbOnly(string databasePath, ReadOnlySpan<char> password)
    {
        InitializeLiteDbCore(databasePath, password);
    }

    /// <summary>
    /// Centralises the five derived paths used by the migration code
    /// so the recovery logic and the rename dance read from the same
    /// definitions. <c>readonly record struct</c> so the compiler
    /// inlines field access without producing a heap allocation.
    /// </summary>
    private readonly record struct MigrationPaths(string Original)
    {
        public string OriginalSalt => Original + ".salt";
        public string TempSqlite => Original + SqliteMigrationTempSuffix;
        public string TempSqliteSalt => TempSqlite + ".salt";
        public string LiteDbBackup => Original + LiteDbBackupSuffix;
        public string LiteDbBackupSalt => LiteDbBackup + ".salt";
    }

    /// <summary>
    /// Outcome of <see cref="TryRecoverFromInterruptedMigration"/>.
    /// </summary>
    private enum InterruptedMigrationOutcome
    {
        /// <summary>
        /// No <c>.litedb-backup</c> present, or the on-disk state is
        /// already a fully-migrated SQLite DB; no recovery action
        /// taken and the caller should proceed normally.
        /// </summary>
        NoInterruption,

        /// <summary>
        /// An interrupted migration was detected and reversed. The
        /// LiteDB database has been restored to the original path;
        /// the caller can now run a fresh migration.
        /// </summary>
        RecoveredAsLiteDb,

        /// <summary>
        /// An interrupted migration was detected and completed. The
        /// SQLite database (with its salt) is now at the original
        /// path; the caller should NOT run migration.
        /// </summary>
        RecoveredAsSqlite,
    }

    /// <summary>
    /// Detects and repairs partial-migration on-disk state caused by a
    /// process crash or power loss between the four File.Move calls
    /// in the migration's rename dance. Without this method, certain
    /// crash windows resulted in silent data loss because LiteDB's
    /// constructor will create a fresh empty database file at any
    /// missing path - the next launch would migrate that empty
    /// database into SQLite and orphan the user's real data at
    /// <c>.litedb-backup</c>.
    /// </summary>
    /// <remarks>
    /// State machine keyed on the presence/absence of
    /// <c>databasePath</c> and <c>databasePath.litedb-backup</c>:
    /// <list type="table">
    ///   <listheader>
    ///     <term>db / bk / tmp state</term>
    ///     <description>Diagnosis and action</description>
    ///   </listheader>
    ///   <item>
    ///     <term>bk absent</term>
    ///     <description>Steady state (never migrated, OR migration completed and user deleted the backup). NoInterruption.</description>
    ///   </item>
    ///   <item>
    ///     <term>db absent, bk present</term>
    ///     <description>Crash after step 1 or step 1b. Reverse the moves; restore LiteDB to the original path. RecoveredAsLiteDb.</description>
    ///   </item>
    ///   <item>
    ///     <term>db present + db.salt absent + tmpsalt present + bk present</term>
    ///     <description>Crash after step 2 (SQLite DB moved in, salt rename pending). Complete step 3. RecoveredAsSqlite.</description>
    ///   </item>
    ///   <item>
    ///     <term>db present + db.salt present + bk present</term>
    ///     <description>Successful prior migration with stale backup. NoInterruption (the user just hasn't deleted the backup yet).</description>
    ///   </item>
    /// </list>
    ///
    /// <para>
    /// The method touches files only via <see cref="File.Move(string, string)"/>
    /// and <see cref="File.Delete(string)"/>; it does NOT open
    /// either database to verify identity, because doing so would
    /// require the password and recovery must work without one (the
    /// caller's password may be wrong; recovery is purely structural).
    /// </para>
    /// </remarks>
    private static InterruptedMigrationOutcome TryRecoverFromInterruptedMigration(
        MigrationPaths paths)
    {
        var dbExists = File.Exists(paths.Original);
        var bkExists = File.Exists(paths.LiteDbBackup);

        if (!bkExists)
        {
            // No prior migration ever touched this path, or the user
            // deleted the .litedb-backup. Nothing to recover.
            return InterruptedMigrationOutcome.NoInterruption;
        }

        if (!dbExists)
        {
            // Crash after step 1 (or step 1b). The LiteDB DB is at
            // .litedb-backup; restore it. Then drop any temp SQLite
            // artefacts so the fresh migration starts clean.
            File.Move(paths.LiteDbBackup, paths.Original);
            if (File.Exists(paths.LiteDbBackupSalt))
            {
                // Step 1b had also fired. Restore the LiteDB salt.
                // OK to overwrite paths.OriginalSalt only if it does
                // NOT already exist - if it does exist that means we
                // crashed BETWEEN step 1 and step 1b and the original
                // salt is still in place. Defer to the original.
                if (!File.Exists(paths.OriginalSalt))
                {
                    File.Move(paths.LiteDbBackupSalt, paths.OriginalSalt);
                }
                else
                {
                    // Step 1 fired but step 1b did not, AND somehow a
                    // backup salt also exists (e.g. from an even earlier
                    // failed attempt). Discard the backup-salt copy;
                    // the in-place salt is the source of truth. Salt
                    // material is secret-bearing so we route through
                    // TrySecureDelete (single random pass + Flush).
                    FileSystemHelper.TrySecureDelete(paths.LiteDbBackupSalt);
                }
            }
            FileSystemHelper.TrySecureDelete(paths.TempSqlite);
            FileSystemHelper.TrySecureDelete(paths.TempSqlite + "-wal");
            FileSystemHelper.TrySecureDelete(paths.TempSqlite + "-shm");
            FileSystemHelper.TrySecureDelete(paths.TempSqliteSalt);
            return InterruptedMigrationOutcome.RecoveredAsLiteDb;
        }

        // db AND bk both exist. Two sub-cases.
        var dbSaltExists = File.Exists(paths.OriginalSalt);
        var tmpSaltExists = File.Exists(paths.TempSqliteSalt);

        if (!dbSaltExists && tmpSaltExists)
        {
            // Crash after step 2: SQLite DB is at databasePath but its
            // salt is still at .sqlite-tmp.salt. Complete step 3
            // (move salt) and step 4 (delete backup).
            File.Move(paths.TempSqliteSalt, paths.OriginalSalt);
            // Tidy any stale wal/shm sidecars from the temp path.
            FileSystemHelper.TrySecureDelete(paths.TempSqlite + "-wal");
            FileSystemHelper.TrySecureDelete(paths.TempSqlite + "-shm");
            FileSystemHelper.TrySecureDelete(paths.LiteDbBackup);
            FileSystemHelper.TrySecureDelete(paths.LiteDbBackupSalt);
            return InterruptedMigrationOutcome.RecoveredAsSqlite;
        }

        if (dbSaltExists)
        {
            // Crash after step 3: SQLite DB + salt are at the target
            // path; only step 4 (delete backup) is missing. Complete
            // step 4 and report recovery as SQLite so the caller skips
            // a redundant fresh migration. The caller's downstream
            // probe will confirm the SQLite DB opens cleanly.
            //
            // C-5 note: under the prior retention policy this state was
            // the steady state (.litedb-backup retained for manual
            // rollback). With C-5's delete-on-success policy this is
            // now an interrupted-after-step-3 state and we clean up.
            FileSystemHelper.TrySecureDelete(paths.LiteDbBackup);
            FileSystemHelper.TrySecureDelete(paths.LiteDbBackupSalt);
            return InterruptedMigrationOutcome.RecoveredAsSqlite;
        }

        // db present but neither db.salt nor tmpsalt: extremely odd
        // state (SQLite DB with no companion salt anywhere). Most
        // likely the user deleted .salt manually. Fresh migration
        // would fail because db is SQLite-shaped and InitializeLiteDbOnly
        // would throw. Best to surface that error rather than silently
        // delete the backup. Leave bk in place and return NoInterruption;
        // the caller's MigrateFromLiteDb will throw downstream.
        return InterruptedMigrationOutcome.NoInterruption;
    }
}
