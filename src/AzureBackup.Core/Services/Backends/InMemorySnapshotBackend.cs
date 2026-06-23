using AzureBackup.Crypto;
using AzureBackup.SqliteInterop;
using Microsoft.Data.Sqlite;

namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// <see cref="IDatabaseBackend"/> implementation that runs the catalog as an
/// <b>in-memory SQLite</b> database and persists it as a single
/// <b>AES-256-GCM-encrypted snapshot</b> file (see
/// <see cref="DbSnapshotEnvelope"/>). This replaces SQLCipher's transparent
/// page-level encryption with application-level encryption owned by managed
/// code, so the application can run on the modern (CVE-fixed) <c>e_sqlite3</c>
/// engine without depending on the unmaintained SQLCipher native bundle.
///
/// <para>
/// <b>Security property.</b> The plaintext database exists only in process
/// memory while unlocked. The only artifact written to disk is the encrypted
/// snapshot produced by <see cref="DbSnapshotEnvelope.Encrypt"/>; no plaintext
/// SQLite file or temp ever touches disk.
/// </para>
///
/// <para>
/// <b>Reuse.</b> Every table/SQL operation is inherited unchanged from
/// <see cref="SqliteBackend"/> (they only touch the shared
/// <see cref="SqliteBackend._connection"/> and lock helpers, which are
/// engine-agnostic). This class overrides only the lifecycle surface:
/// <see cref="Initialize"/> (open in-memory + load snapshot or create schema),
/// <see cref="Checkpoint"/> (serialize + encrypt + atomic write),
/// <see cref="Close"/> / <see cref="Dispose"/> (final persist + dispose), and
/// <see cref="SecureReset"/> (delete the snapshot).
/// </para>
///
/// <para>
/// <b>Size bound.</b> Because the database is held in memory, its size is bound
/// by available RAM (an accepted trade-off of this design).
/// </para>
/// </summary>
internal sealed class InMemorySnapshotBackend : SqliteBackend
{
    // A unique in-memory data source name per instance so two backends never
    // share the same in-memory database. Mode=Memory + a private cache keeps it
    // isolated and process-local.
    private readonly string _memoryDataSource = "azbk-snapshot-" + Guid.NewGuid().ToString("N");

    private string? _snapshotPath;

    // The snapshot encryption key (32 bytes) and its salt are derived ONCE at
    // Initialize and cached for the open lifetime so each Checkpoint/Close does
    // NOT pay the ~64 MB Argon2id cost again. The password is never retained.
    // A fresh random nonce is still generated per write (see DbSnapshotEnvelope).
    // Both are zeroed on Close/Dispose/SecureReset.
    private byte[]? _snapshotKey;
    private byte[]? _snapshotSalt;

    // Set once Close/SecureReset has run so a subsequent Dispose does not persist
    // a second time (idempotent, avoids a redundant serialize + write).
    private bool _closed;

    /// <summary>
    /// Opens an in-memory SQLite database and either loads the existing
    /// encrypted snapshot at <paramref name="databasePath"/> into it (decrypting
    /// with <paramref name="password"/>) or creates the schema fresh when no
    /// snapshot exists. The Argon2id snapshot key is derived once here and cached
    /// for the session.
    /// </summary>
    public override void Initialize(string databasePath, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty", nameof(password));

        // NOTE: at this phase Core still references only the SQLCipher bundle, so
        // the in-memory connection runs on that engine (used WITHOUT any cipher
        // PRAGMA, i.e. as a plain SQLite engine). When Core is made
        // SQLCipher-free and switched to bundle_e_sqlite3 3.x (the CVE-fixed
        // engine), this backend automatically runs on the modern engine with no
        // code change here.
        _databasePath = databasePath;
        _snapshotPath = databasePath;
        _closed = false;
        EmitDiag($"InMemorySnapshotBackend.Initialize: starting (path={databasePath})");

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var snapshotExists = File.Exists(_snapshotPath) && new FileInfo(_snapshotPath).Length > 0;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _memoryDataSource,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            _connection = connection;

            if (snapshotExists)
            {
                EmitDiag("InMemorySnapshotBackend.Initialize: loading existing snapshot");
                LoadSnapshotInto(connection, _snapshotPath, password, out _snapshotKey, out _snapshotSalt);
            }
            else
            {
                // Fresh catalog: derive a key from a new random salt once; reused
                // for every persist this session.
                _snapshotSalt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(KdfParameters.SaltSize);
                _snapshotKey = Argon2idDeriver.DeriveKey(password, _snapshotSalt, "database snapshot key",
                    diag: msg => EmitDiag(msg));
            }

            ApplyPragmas();
            CreateSchema();

            if (!snapshotExists)
            {
                // Persist the freshly-created (empty-schema) catalog immediately so
                // a snapshot file exists on disk right after Initialize, matching
                // the prior backend's contract that DatabaseExists is true once the
                // catalog has been initialized. The file is the encrypted AZDB
                // snapshot -- no plaintext touches disk.
                PersistSnapshotUnlocked();
            }

            EmitDiag("InMemorySnapshotBackend.Initialize: completed successfully");
        }
        catch
        {
            // On any failure leave no half-open connection or retained secret.
            _connection = null;
            try { connection.Dispose(); } catch { /* best effort */ }
            ZeroKeyMaterial();
            throw;
        }
    }

    /// <summary>
    /// Persists the current in-memory database to the encrypted snapshot on
    /// disk. Serializes the database to a byte image in memory, encrypts it with
    /// <see cref="DbSnapshotEnvelope"/>, and writes it atomically (temp file +
    /// flush + rename) so an interrupted write never destroys the prior good
    /// snapshot. No plaintext touches disk.
    /// </summary>
    public override void Checkpoint()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        InWriteLock(() => PersistSnapshotUnlocked());
    }

    /// <summary>Persists and then closes the in-memory database. Idempotent.</summary>
    public override void Close()
    {
        EmitDiag("InMemorySnapshotBackend.Close: enter");
        try
        {
            _writeLock.EnterWriteLock();
        }
        catch
        {
            // Lock already disposed; nothing safe to persist.
            CloseInMemoryConnection();
            ZeroKeyMaterial();
            _closed = true;
            return;
        }

        try
        {
            // Persist only the FIRST time Close runs (a later Dispose-after-Close
            // must not serialize + write a second time).
            if (_connection != null && !_closed)
            {
                try { PersistSnapshotUnlocked(); }
                catch (Exception ex) { EmitDiag($"InMemorySnapshotBackend.Close: final persist failed (best-effort) -- {ex.GetType().Name}: {ex.Message}"); }
            }
            CloseInMemoryConnection();
            _closed = true;
        }
        finally
        {
            try { _writeLock.ExitWriteLock(); } catch { /* lock already gone */ }
            ZeroKeyMaterial();
        }
        EmitDiag("InMemorySnapshotBackend.Close: exit");
    }

    /// <summary>
    /// Closes the in-memory database and securely deletes the on-disk snapshot.
    /// Does NOT persist (the database is being destroyed). Because the database
    /// is in memory, there are no WAL/SHM or salt sidecar files to remove --
    /// only the single snapshot file.
    /// </summary>
    public override void SecureReset()
    {
        var path = _snapshotPath;
        EmitDiag($"InMemorySnapshotBackend.SecureReset: enter (path={path ?? "(null)"})");

        // Scrub sensitive config columns in memory before discarding the DB, so
        // even a process-memory dump after this point has no secrets.
        if (_connection != null)
        {
            try
            {
                InWriteLock(() =>
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = """
                        UPDATE config
                        SET encrypted_connection_string = NULL,
                            password_salt = NULL,
                            password_verification_hash = NULL
                        WHERE id = 1;
                        """;
                    cmd.ExecuteNonQuery();
                });
            }
            catch (Exception ex)
            {
                EmitDiag($"InMemorySnapshotBackend.SecureReset: scrub failed (best-effort) -- {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Close WITHOUT persisting -- we are destroying the database.
        try { _writeLock.EnterWriteLock(); } catch { CloseInMemoryConnection(); ZeroKeyMaterial(); _closed = true; }
        try { CloseInMemoryConnection(); _closed = true; }
        finally { try { _writeLock.ExitWriteLock(); } catch { } ZeroKeyMaterial(); }

        if (!string.IsNullOrEmpty(path))
        {
            FileSystemHelper.TrySecureDelete(path);
            EmitDiag("InMemorySnapshotBackend.SecureReset: snapshot deleted");
        }

        _snapshotPath = null;
        _databasePath = null;
        EmitDiag("InMemorySnapshotBackend.SecureReset: completed");
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        Close();
        try { _writeLock.Dispose(); } catch { /* terminal best-effort */ }
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// Serializes the in-memory database, encrypts it with the cached session
    /// key (no per-write KDF), and writes it atomically. Caller must hold the
    /// write lock.
    /// </summary>
    private void PersistSnapshotUnlocked()
    {
        if (_connection == null || _snapshotPath == null || _snapshotKey == null || _snapshotSalt == null)
            return;

        var image = SqliteSerialization.Serialize(_connection);
        try
        {
            // Uses the cached key + salt; a fresh nonce is generated per write
            // inside the envelope, so the (key, nonce) pair is never reused.
            var encrypted = DbSnapshotEnvelope.Encrypt(image, _snapshotKey, _snapshotSalt);
            AtomicFile.WriteAllBytesAtomic(_snapshotPath, encrypted);
            EmitDiag($"InMemorySnapshotBackend: persisted snapshot ({encrypted.Length} bytes)");
        }
        finally
        {
            // The serialized image is plaintext catalog data; zero it promptly.
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(image);
        }
    }

    /// <summary>
    /// Decrypts the snapshot at <paramref name="path"/> and deserializes it into
    /// the open in-memory <paramref name="connection"/>, returning the derived
    /// key and salt so the backend can re-encrypt subsequent snapshots without
    /// re-running the KDF.
    /// </summary>
    private static void LoadSnapshotInto(
        SqliteConnection connection, string path, ReadOnlySpan<char> password,
        out byte[] key, out byte[] salt)
    {
        var encrypted = File.ReadAllBytes(path);
        byte[] image;
        try
        {
            image = DbSnapshotEnvelope.DecryptAndExtractKey(encrypted, password, out key, out salt);
        }
        catch (DbSnapshotException ex)
        {
            // A snapshot authentication failure means the password is wrong (or
            // the file was tampered with). Surface the same contract the unlock
            // flow expects so the UI shows the standard retry prompt rather than a
            // raw crypto error.
            throw new InvalidPasswordException("Invalid password. Please try again.", ex);
        }
        try
        {
            SqliteSerialization.DeserializeInto(connection, image);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(image);
        }
    }

    private void CloseInMemoryConnection()
    {
        if (_connection != null)
        {
            try { _connection.Dispose(); }
            catch (Exception ex) { EmitDiag($"InMemorySnapshotBackend: connection.Dispose threw -- {ex.GetType().Name}: {ex.Message}"); }
            _connection = null;
        }
    }

    private void ZeroKeyMaterial()
    {
        if (_snapshotKey != null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_snapshotKey);
            _snapshotKey = null;
        }
        // The salt is not secret, but clear the reference so a reused instance
        // cannot accidentally encrypt with a stale salt after close.
        _snapshotSalt = null;
    }

    /// <summary>
    /// Snapshot-format file-integrity check. The on-disk artefact is an
    /// AES-256-GCM AZDB envelope, NOT a SQLCipher database, so
    /// <c>PRAGMA cipher_integrity_check</c> does not apply (and would fail on the
    /// modern engine). The envelope's authentication tag already guarantees the
    /// ciphertext was not altered -- a tampered snapshot fails to decrypt at
    /// <see cref="Initialize"/> time and the backend never opens. We therefore
    /// report cipher integrity as OK (no messages) and run stock
    /// <c>PRAGMA integrity_check</c> against the decrypted in-memory image to
    /// surface any b-tree damage that may have been serialized into the snapshot.
    /// </summary>
    public override DatabaseFileIntegrityResult CheckDatabaseFileIntegrity()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        EmitDiag("InMemorySnapshotBackend.CheckDatabaseFileIntegrity: enter");
        return InWriteLock(() =>
        {
            // Empty cipher messages == OK: the AES-256-GCM tag authenticated the
            // snapshot at load, so reaching this point already proves the
            // ciphertext is intact.
            var sqliteMessages = RunPragmaIntegrityCheck("integrity_check");
            var result = new DatabaseFileIntegrityResult(
                CipherIntegrityMessages: Array.Empty<string>(),
                SqliteIntegrityMessages: sqliteMessages);
            EmitDiag(
                $"InMemorySnapshotBackend.CheckDatabaseFileIntegrity: complete " +
                $"(sqliteOk={result.SqliteOk}, sqliteRows={sqliteMessages.Count})");
            return result;
        });
    }

    /// <summary>
    /// Snapshot-format index repair. Identical to the base whitelist-gated
    /// REINDEX, except it does NOT consult <c>cipher_integrity_check</c> (which
    /// does not exist on the modern engine) and re-runs only stock
    /// <c>integrity_check</c> for the post-repair diagnosis. Because the snapshot
    /// is re-encrypted in full on every persist, a successful REINDEX is written
    /// back into the next snapshot like any other change.
    /// </summary>
    public override DatabaseRepairResult ReindexCorruptIndexes(DatabaseFileIntegrityResult diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        EmitDiag("InMemorySnapshotBackend.ReindexCorruptIndexes: enter");

        if (diagnosis.IsHealthy)
        {
            EmitDiag("InMemorySnapshotBackend.ReindexCorruptIndexes: aborted - diagnosis reported healthy");
            return DatabaseRepairResult.NotAttempted(
                "Repair not needed: the supplied diagnosis reports the database is healthy.");
        }

        var classification = DatabaseRepairClassifier.TryClassify(diagnosis.SqliteIntegrityMessages);
        if (!classification.AllRepairable)
        {
            EmitDiag(
                "InMemorySnapshotBackend.ReindexCorruptIndexes: aborted - one or more findings are outside the REINDEX-safe whitelist " +
                $"(unrepairableCount={classification.UnrepairableMessages.Count})");
            return DatabaseRepairResult.NotAttempted(
                "Repair refused: at least one integrity_check finding is outside " +
                "the set of damage shapes REINDEX can safely fix. The unrepairable " +
                "findings are:" + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, classification.UnrepairableMessages));
        }

        if (classification.RepairableIndexNames.Count == 0)
        {
            EmitDiag("InMemorySnapshotBackend.ReindexCorruptIndexes: aborted - classifier extracted zero index names");
            return DatabaseRepairResult.NotAttempted(
                "Repair refused: integrity_check reported failures but the classifier " +
                "could not extract any specific index names to repair. This indicates " +
                "an unfamiliar message format; please file a support request with the " +
                "verification report.");
        }

        return InWriteLock(() =>
        {
            var knownIndexNames = ReadAllIndexNames();
            var attempted = new List<string>();
            var succeeded = new List<string>();
            var failed = new List<string>();

            foreach (var name in classification.RepairableIndexNames)
            {
                if (!knownIndexNames.Contains(name))
                {
                    failed.Add($"{name}: not present in sqlite_master (skipped, not reindexed)");
                    continue;
                }

                attempted.Add(name);
                try
                {
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = $"REINDEX \"{name.Replace("\"", "\"\"")}\";";
                    cmd.ExecuteNonQuery();
                    succeeded.Add(name);
                    EmitDiag($"InMemorySnapshotBackend.ReindexCorruptIndexes: REINDEX {name} succeeded");
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    failed.Add($"{name}: SQLite Error {ex.SqliteErrorCode}: {ex.Message}");
                    EmitDiag($"InMemorySnapshotBackend.ReindexCorruptIndexes: REINDEX {name} failed: {ex.Message}");
                }
            }

            // Snapshot integrity has no cipher dimension; the post-repair
            // diagnosis is cipher-OK (empty) plus the fresh integrity_check.
            var postSqlite = RunPragmaIntegrityCheck("integrity_check");
            var postDiagnosis = new DatabaseFileIntegrityResult(Array.Empty<string>(), postSqlite);

            EmitDiag(
                "InMemorySnapshotBackend.ReindexCorruptIndexes: complete " +
                $"(attempted={attempted.Count}, succeeded={succeeded.Count}, failed={failed.Count}, " +
                $"postIsHealthy={postDiagnosis.IsHealthy})");

            return DatabaseRepairResult.Attempted(
                attemptedIndexes: attempted,
                succeededIndexes: succeeded,
                failedIndexes: failed,
                postRepairDiagnosis: postDiagnosis);
        });
    }
}
