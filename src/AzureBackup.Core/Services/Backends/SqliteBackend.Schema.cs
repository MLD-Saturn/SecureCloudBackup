using System.Security.Cryptography;
using AzureBackup.Crypto;
using Konscious.Security.Cryptography;
using Microsoft.Data.Sqlite;
using static AzureBackup.Core.KdfParameters;
using static AzureBackup.Crypto.Argon2idDeriver;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// B22: schema, KDF, and SQLCipher unlock surface for
/// <see cref="SqliteBackend"/>. Split from the root partial to keep each
/// file focused (the root file owns lifecycle, this one owns the
/// open/setup pipeline).
/// </summary>
internal partial class SqliteBackend
{
    private static byte[] LoadOrCreateSalt(string databasePath)
    {
        var saltFilePath = GetSaltFilePath(databasePath);
        if (File.Exists(saltFilePath))
        {
            var salt = File.ReadAllBytes(saltFilePath);
            if (salt.Length != SaltSize)
            {
                throw new InvalidOperationException(
                    $"Database salt file is corrupted (expected {SaltSize} bytes, got {salt.Length})");
            }
            return salt;
        }

        var fresh = new byte[SaltSize];
        RandomNumberGenerator.Fill(fresh);
        File.WriteAllBytes(saltFilePath, fresh);
        return fresh;
    }

    private byte[] DeriveKeyFromPassword(ReadOnlySpan<char> password, byte[] salt)
        // B22: cannot pass EmitDiag as a method group because it is
        // [Conditional("DIAGNOSTICLOG")] (CS1618). Wrap it in a lambda
        // so the call site is preserved, and let the lambda body be a
        // no-op when DIAGNOSTICLOG is undefined (the EmitDiag call
        // itself is then compiled away).
        => DeriveKeyFromPasswordCore(passwordChars: password, salt: salt, diag: msg => EmitDiag(msg));

    private static byte[] DeriveKeyFromPasswordCore(
        ReadOnlySpan<char> passwordChars, byte[] salt, Action<string>? diag)
        // Delegates to the single shared Argon2id entry point, which owns the
        // password-to-bytes conversion (zeroed), the canonical KdfParameters
        // cost, and the OOM -> LOH-compaction -> retry-once -> throw
        // InsufficientMemoryForKdfException behaviour. Previously this method
        // inlined that logic, duplicating EncryptionService and the snapshot
        // envelope; it now shares one implementation.
        => Argon2idDeriver.DeriveKey(passwordChars, salt, "database key", diag);

    private void OpenAndUnlock(string databasePath, byte[] derivedKey, bool dbExistedBeforeOpen)
    {
        // B10: only allow Create when there is no DB on disk yet. A
        // wrong-password attempt against an EXISTING database must not
        // be able to write a fresh empty file as a side effect; using
        // SqliteOpenMode.ReadWrite (no Create) means connection.Open()
        // throws SqliteException(14, "unable to open database file") on
        // a missing file rather than silently creating one.
        var mode = dbExistedBeforeOpen
            ? SqliteOpenMode.ReadWrite
            : SqliteOpenMode.ReadWriteCreate;
        EmitDiag($"OpenAndUnlock: opening with mode={mode} (dbExistedBeforeOpen={dbExistedBeforeOpen})");
        _connection = OpenAndUnlockCore(
            databasePath, derivedKey, mode, validateKey: true);
        EmitDiag("OpenAndUnlock: connection unlocked successfully");
    }

    private static SqliteConnection OpenAndUnlockCore(
        string databasePath, byte[] derivedKey,
        SqliteOpenMode mode, bool validateKey)
    {
        // W-DB-enc Step 7: AzureBackup.Core no longer ships the SQLCipher native
        // engine -- it references bundle_e_sqlite3 (the CVE-2025-6965-fixed modern
        // engine) ONLY. The SQLCipher `PRAGMA cipher_kdf_algorithm` / `PRAGMA key`
        // sequence below does not exist on the modern engine and issuing it
        // ABORTS the native process (not a catchable exception). The production
        // catalog now opens through InMemorySnapshotBackend (an AES-256-GCM
        // application-level encrypted snapshot loaded into an in-memory DB), which
        // overrides Initialize and never reaches this path. Reading a legacy
        // SQLCipher database is the job of the separate single-engine
        // azurebackup-migrate helper process (which references bundle_e_sqlcipher).
        // Throw a clear managed exception so any remaining caller fails loudly
        // instead of crashing the host.
        throw new NotSupportedException(
            "AzureBackup.Core no longer contains a SQLCipher engine (it uses the " +
            "CVE-fixed bundle_e_sqlite3). Open the catalog via InMemorySnapshotBackend, " +
            "or read a legacy SQLCipher database with the azurebackup-migrate helper process.");
    }

    private protected void ApplyPragmas()
    {
        if (_connection == null) return;

        // WAL journaling: concurrent readers + single writer, fast commits,
        // matches the rationale for Phase 5's RWLock work.
        // foreign_keys: required for ON DELETE CASCADE to work; off by default.
        // synchronous=NORMAL: safe with WAL, much faster than FULL.
        // temp_store=MEMORY: avoid disk for sort/group temporaries.
        // cache_size: -65536 means "65536 KiB of cache" (negative values are KB,
        //   positive values are PAGE COUNT). Default is 2000 pages = 8 MB which
        //   is too small for the rebuild + bulk-insert workloads we measured in
        //   C-3 (3b): the 100K and 500K cells spilled the page cache and paid
        //   significant decrypt-thrash cost. 64 MB sized to comfortably hold
        //   the working set of the largest one-time migration step (~50 MB at
        //   500K chunks). The cache lives in the SQLite/SQLCipher allocator,
        //   not the .NET GC heap, so this does NOT show up as managed
        //   allocations in benchmark results.
        EmitDiag("ApplyPragmas: setting WAL journaling, foreign_keys=ON, synchronous=NORMAL, cache_size=-65536 KiB");
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -65536;
            """;
        cmd.ExecuteNonQuery();
    }

    private protected void CreateSchema()
    {
        if (_connection == null) return;

        EmitDiag("CreateSchema: starting CREATE TABLE IF NOT EXISTS pass");
        // Pure-relational schema (Option C / §4 of docs/option-c-evaluation.md).
        // Ordered so foreign-key dependencies are created before their referrers.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            -- Single-row configuration table; row id is always 1.
            -- Mirrors every field on Models.BackupConfiguration (see C-1c
            -- commit message for the field-by-field mapping). Defaults match
            -- the C# model defaults so a freshly seeded row reads back as the
            -- equivalent of `new BackupConfiguration()`.
            CREATE TABLE IF NOT EXISTS config (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                auth_method INTEGER NOT NULL DEFAULT 1,            -- AzureAuthMethod, 1 = ConnectionString
                storage_account_name TEXT NULL,
                encrypted_connection_string BLOB NULL,
                container_name TEXT NULL DEFAULT 'backup',
                password_salt BLOB NULL,
                password_verification_hash BLOB NULL,
                last_backup_time TEXT NULL,
                total_bytes_uploaded INTEGER NOT NULL DEFAULT 0,
                failed_login_attempts INTEGER NOT NULL DEFAULT 0,
                lockout_until_ticks INTEGER NULL,
                is_entra_id_authenticated INTEGER NOT NULL DEFAULT 0,
                entra_id_user_name TEXT NULL,
                config_version INTEGER NOT NULL DEFAULT 3,
                memory_limit_enabled INTEGER NOT NULL DEFAULT 1,
                -- B29: NULL = "user has not yet expressed a preference,
                -- compute a hardware-aware default at read time via
                -- SystemMemoryHelper.GetRecommendedDefaultLimitMB". A
                -- non-NULL integer here is always a value the user
                -- explicitly saved through the Settings UI (or that
                -- migrated forward from a pre-B29 database). Existing
                -- B27/B28 databases keep their stored 8192 (or whatever
                -- the user set) -- DEFAULT only fires for newly
                -- inserted rows, so no existing user is silently
                -- re-configured.
                memory_limit_mb INTEGER NULL DEFAULT NULL,
                schema_version INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS watched_folders (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE,
                storage_tier INTEGER NOT NULL DEFAULT 0,
                is_enabled INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS watched_folder_excludes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                folder_id INTEGER NOT NULL,
                pattern TEXT NOT NULL,
                kind INTEGER NOT NULL,  -- 0 = ExcludePatterns, 1 = ExcludeSubfolders
                FOREIGN KEY (folder_id) REFERENCES watched_folders(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_watched_folder_excludes_folder
                ON watched_folder_excludes(folder_id);

            CREATE TABLE IF NOT EXISTS global_exclude_patterns (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pattern TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                local_path TEXT NOT NULL UNIQUE,
                blob_name TEXT NOT NULL DEFAULT '',
                file_size INTEGER NOT NULL,
                last_modified TEXT NOT NULL,
                file_hash TEXT NOT NULL DEFAULT '',
                status INTEGER NOT NULL,
                backed_up_at TEXT NOT NULL,
                metadata_version INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS idx_files_status ON files(status);
            CREATE INDEX IF NOT EXISTS idx_files_file_hash ON files(file_hash);

            CREATE TABLE IF NOT EXISTS file_chunks (
                file_id INTEGER NOT NULL,
                chunk_order INTEGER NOT NULL,
                chunk_index INTEGER NOT NULL,
                offset INTEGER NOT NULL,
                length INTEGER NOT NULL,
                hash TEXT NOT NULL,
                blob_name TEXT NULL,
                PRIMARY KEY (file_id, chunk_order),
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_file_chunks_hash ON file_chunks(hash);

            CREATE TABLE IF NOT EXISTS pending_changes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL,
                change_type INTEGER NOT NULL,
                detected_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_pending_changes_path ON pending_changes(file_path);

            CREATE TABLE IF NOT EXISTS chunk_index (
                chunk_hash TEXT PRIMARY KEY,
                first_uploaded_at TEXT NOT NULL,
                original_uploader_path TEXT NOT NULL DEFAULT '',
                size_bytes INTEGER NOT NULL,
                reference_count INTEGER NOT NULL,
                current_tier INTEGER NOT NULL DEFAULT 0,
                last_verified_at TEXT NOT NULL,
                -- D6: MD5 of the encrypted blob as stored in Azure
                -- (matches what GetChunkPropertiesAsync returns as
                -- ContentHash). Stamped at upload time; null for
                -- chunks uploaded before D6. The integrity check's
                -- T1 tier compares this against the live Azure-side
                -- hash so same-size envelope corruption is detected
                -- without escalating to a full T2 download.
                expected_encrypted_md5 BLOB NULL
            );
            CREATE INDEX IF NOT EXISTS idx_chunk_index_refcount ON chunk_index(reference_count);
            CREATE INDEX IF NOT EXISTS idx_chunk_index_tier ON chunk_index(current_tier);

            -- Reverse index built in Phase 5 / P3; replaces the redundant
            -- ReferencingFiles list on ChunkIndexEntry (eval doc / §4:
            -- "What we drop from the LiteDB schema").
            CREATE TABLE IF NOT EXISTS chunk_file_refs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path TEXT NOT NULL,
                chunk_hash TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                referenced_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_chunk_file_refs_path ON chunk_file_refs(file_path);
            CREATE INDEX IF NOT EXISTS idx_chunk_file_refs_hash ON chunk_file_refs(chunk_hash);

            CREATE TABLE IF NOT EXISTS index_metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            -- Persisted history of post-backup integrity checks (D1).
            -- Retention: keep most recent 30 rows; older pruned by
            -- LocalDatabaseService.PruneIntegrityCheckRuns. Per-failure
            -- detail lives in integrity_check_failures, keyed by run_id.
            -- The companion .diag files in the diagnostics/ folder are
            -- the authoritative source for chunk-level traces.
            CREATE TABLE IF NOT EXISTS integrity_check_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_utc TEXT NOT NULL,
                finished_utc TEXT NULL,
                session_id TEXT NOT NULL,
                scope_summary TEXT NOT NULL,
                files_checked INTEGER NOT NULL DEFAULT 0,
                files_passed INTEGER NOT NULL DEFAULT 0,
                files_failed_t1 INTEGER NOT NULL DEFAULT 0,
                files_failed_t2 INTEGER NOT NULL DEFAULT 0,
                files_failed_t3 INTEGER NOT NULL DEFAULT 0,
                files_warning INTEGER NOT NULL DEFAULT 0,
                files_auto_repaired INTEGER NOT NULL DEFAULT 0,
                cancelled INTEGER NOT NULL DEFAULT 0,
                parent_run_id INTEGER NULL,
                diag_bundle_path TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_integrity_check_runs_started ON integrity_check_runs(started_utc DESC);

            -- Per-file failure rows for the LATEST run only. When a new
            -- run starts the engine deletes every row in this table whose
            -- run_id != the new run id, keeping the failure panel
            -- responsive even if the user has dozens of historical runs.
            CREATE TABLE IF NOT EXISTS integrity_check_failures (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id INTEGER NOT NULL,
                file_id INTEGER NOT NULL,
                local_path TEXT NOT NULL,
                failure_tier INTEGER NOT NULL,
                failure_reason TEXT NOT NULL,
                chunk_hash TEXT NULL,
                detail TEXT NOT NULL,
                diag_file_path TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_integrity_check_failures_run ON integrity_check_failures(run_id);
            CREATE INDEX IF NOT EXISTS idx_integrity_check_failures_tier ON integrity_check_failures(failure_tier);
            """;
        cmd.ExecuteNonQuery();

        // D6 backfill: idempotently add expected_encrypted_md5 to chunk_index
        // for databases created before D6. Skipped when the column already
        // exists (CREATE TABLE IF NOT EXISTS above declares it for fresh
        // installs). Pre-existing chunks remain null until they get
        // re-uploaded; the integrity check engine treats null as "T1
        // cannot decide -- pass" so legacy chunks still pass the cheap
        // tier (escalation to T2 is the user's lever).
        using (var probeCmd = _connection.CreateCommand())
        {
            probeCmd.CommandText = "SELECT 1 FROM pragma_table_info('chunk_index') WHERE name='expected_encrypted_md5';";
            var present = probeCmd.ExecuteScalar();
            if (present == null)
            {
                EmitDiag("CreateSchema: D6 backfill -- adding expected_encrypted_md5 column to chunk_index");
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE chunk_index ADD COLUMN expected_encrypted_md5 BLOB NULL;";
                alterCmd.ExecuteNonQuery();
            }
        }

        // B43 backfill: idempotently add files_auto_repaired to
        // integrity_check_runs for databases created before B43. Default
        // 0 reflects the historical truth: pre-B43 runs had no
        // auto-repair counter to record (B42 introduced auto-repair but
        // only stored the count in-memory on the IntegrityCheckRun
        // returned to the caller). Existing rows therefore correctly
        // report 0 auto-repaired files; only B43+ runs persist a real
        // count.
        using (var probeCmd = _connection.CreateCommand())
        {
            probeCmd.CommandText = "SELECT 1 FROM pragma_table_info('integrity_check_runs') WHERE name='files_auto_repaired';";
            var present = probeCmd.ExecuteScalar();
            if (present == null)
            {
                EmitDiag("CreateSchema: B43 backfill -- adding files_auto_repaired column to integrity_check_runs");
                using var alterCmd = _connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE integrity_check_runs ADD COLUMN files_auto_repaired INTEGER NOT NULL DEFAULT 0;";
                alterCmd.ExecuteNonQuery();
            }
        }

        // Seed the singleton config row so the wrong-password probe in
        // OpenAndUnlock has a real user-table read to perform on reopen.
        // INSERT OR IGNORE keeps this idempotent across reopens; the row
        // takes its column defaults so reading immediately gives back
        // the equivalent of `new BackupConfiguration()`.
        using var seed = _connection.CreateCommand();
        seed.CommandText = """
            INSERT OR IGNORE INTO config (id) VALUES (1);
            """;
        seed.ExecuteNonQuery();
        EmitDiag("CreateSchema: completed");
    }
}
