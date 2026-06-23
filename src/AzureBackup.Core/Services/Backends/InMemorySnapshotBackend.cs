using System.Runtime.InteropServices;
using AzureBackup.Crypto;
using Microsoft.Data.Sqlite;
using SQLitePCL;

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

    // The password is retained (as a char[]) for the lifetime of the open
    // backend because every persist (Checkpoint/Close) must re-derive the
    // snapshot key. Zeroed on Close/Dispose/SecureReset.
    private char[]? _password;

    /// <summary>
    /// Opens an in-memory SQLite database and either loads the existing
    /// encrypted snapshot at <paramref name="databasePath"/> into it (decrypting
    /// with <paramref name="password"/>) or creates the schema fresh when no
    /// snapshot exists.
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
        _password = password.ToArray();
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
                LoadSnapshotInto(connection, _snapshotPath, password);
            }

            ApplyPragmas();
            CreateSchema();
            EmitDiag("InMemorySnapshotBackend.Initialize: completed successfully");
        }
        catch
        {
            // On any failure leave no half-open connection or retained secret.
            _connection = null;
            try { connection.Dispose(); } catch { /* best effort */ }
            ZeroPassword();
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

    /// <summary>Persists and then closes the in-memory database.</summary>
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
            ZeroPassword();
            return;
        }

        try
        {
            if (_connection != null)
            {
                try { PersistSnapshotUnlocked(); }
                catch (Exception ex) { EmitDiag($"InMemorySnapshotBackend.Close: final persist failed (best-effort) -- {ex.GetType().Name}: {ex.Message}"); }
            }
            CloseInMemoryConnection();
        }
        finally
        {
            try { _writeLock.ExitWriteLock(); } catch { /* lock already gone */ }
            ZeroPassword();
        }
        EmitDiag("InMemorySnapshotBackend.Close: exit");
    }

    /// <summary>
    /// Closes the in-memory database (persisting first) and securely deletes the
    /// on-disk snapshot. Because the database is in memory, there are no WAL/SHM
    /// or salt sidecar files to remove -- only the single snapshot file.
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
        try { _writeLock.EnterWriteLock(); } catch { CloseInMemoryConnection(); ZeroPassword(); }
        try { CloseInMemoryConnection(); }
        finally { try { _writeLock.ExitWriteLock(); } catch { } ZeroPassword(); }

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
    /// Serializes the in-memory database, encrypts it, and writes it atomically.
    /// Caller must hold the write lock.
    /// </summary>
    private void PersistSnapshotUnlocked()
    {
        if (_connection == null || _snapshotPath == null || _password == null)
            return;

        var image = SerializeDatabase(_connection);
        try
        {
            var encrypted = DbSnapshotEnvelope.Encrypt(image, _password);
            AtomicWrite(_snapshotPath, encrypted);
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
    /// the open in-memory <paramref name="connection"/>.
    /// </summary>
    private static void LoadSnapshotInto(SqliteConnection connection, string path, ReadOnlySpan<char> password)
    {
        var encrypted = File.ReadAllBytes(path);
        var image = DbSnapshotEnvelope.Decrypt(encrypted, password);
        try
        {
            DeserializeInto(connection, image);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(image);
        }
    }

    /// <summary>
    /// Serializes the "main" database of <paramref name="connection"/> to a
    /// managed byte image via <c>sqlite3_serialize</c>, copying out of the
    /// SQLite-owned buffer and freeing it.
    /// </summary>
    private static byte[] SerializeDatabase(SqliteConnection connection)
    {
        var handle = GetSqliteHandle(connection);
        nint ptr = raw.sqlite3_serialize(handle, "main", out long length, 0);
        if (ptr == 0 || length <= 0)
            throw new InvalidOperationException($"sqlite3_serialize failed (ptr={ptr}, len={length}).");
        try
        {
            var image = new byte[length];
            Marshal.Copy(ptr, image, 0, checked((int)length));
            return image;
        }
        finally
        {
            raw.sqlite3_free(ptr);
        }
    }

    /// <summary>
    /// Deserializes <paramref name="image"/> into the "main" database of
    /// <paramref name="connection"/>. The image is copied into a
    /// SQLite-allocated buffer that SQLite owns and frees on connection close
    /// (<c>FREEONCLOSE</c>).
    /// </summary>
    private static void DeserializeInto(SqliteConnection connection, byte[] image)
    {
        var handle = GetSqliteHandle(connection);
        nint buf = raw.sqlite3_malloc(image.Length);
        if (buf == 0)
            throw new InvalidOperationException("sqlite3_malloc failed for snapshot deserialize.");
        // From here SQLite owns 'buf' (FREEONCLOSE), even on error after deserialize.
        Marshal.Copy(image, 0, buf, image.Length);
        int rc = raw.sqlite3_deserialize(
            handle, "main", buf,
            image.Length, image.Length,
            raw.SQLITE_DESERIALIZE_RESIZEABLE | raw.SQLITE_DESERIALIZE_FREEONCLOSE);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"sqlite3_deserialize failed (rc={rc}).");
    }

    /// <summary>
    /// Reaches the underlying <see cref="sqlite3"/> handle from a
    /// <see cref="SqliteConnection"/>. Microsoft.Data.Sqlite exposes it via an
    /// internal member whose exact name varies by version, so it is located by
    /// TYPE rather than name (the literal "Handle" name is null in some 10.x
    /// builds).
    /// </summary>
    private static sqlite3 GetSqliteHandle(SqliteConnection connection)
    {
        var t = typeof(SqliteConnection);
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance;
        foreach (var p in t.GetProperties(Flags))
            if (typeof(sqlite3).IsAssignableFrom(p.PropertyType) && p.GetValue(connection) is sqlite3 s)
                return s;
        foreach (var f in t.GetFields(Flags))
            if (typeof(sqlite3).IsAssignableFrom(f.FieldType) && f.GetValue(connection) is sqlite3 s)
                return s;
        throw new InvalidOperationException(
            "Could not locate the sqlite3 handle on SqliteConnection (Microsoft.Data.Sqlite internals changed).");
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> atomically:
    /// a temp file in the same directory is written and flushed to disk, then
    /// renamed over the destination so a crash mid-write leaves the previous
    /// good snapshot intact.
    /// </summary>
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var temp = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            // File.Move with overwrite is atomic on the same volume on modern .NET.
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
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

    private void ZeroPassword()
    {
        if (_password != null)
        {
            Array.Clear(_password);
            _password = null;
        }
    }
}
