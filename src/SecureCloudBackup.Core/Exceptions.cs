namespace SecureCloudBackup.Core;

/// <summary>
/// Exception thrown when data integrity verification fails.
/// This indicates potential corruption, tampering, or incorrect password.
/// </summary>
public class DataIntegrityException : Exception
{
    public string? AffectedResource { get; }

    public DataIntegrityException(string message) : base(message)
    {
    }

    public DataIntegrityException(string message, string affectedResource) : base(message)
    {
        AffectedResource = affectedResource;
    }

    public DataIntegrityException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public DataIntegrityException(string message, string affectedResource, Exception innerException) 
        : base(message, innerException)
    {
        AffectedResource = affectedResource;
    }
}

/// <summary>
/// Specialization of <see cref="DataIntegrityException"/> for a download-time
/// Content-MD5 mismatch: the encrypted bytes that arrived do not match the
/// blob's stored Content-MD5. This is almost always transient in-transit
/// corruption, so the restore pipeline re-downloads the affected chunk a
/// bounded number of times before the data is handed to the best-effort
/// recovery path. It is a distinct type so the retry classifier can target
/// ONLY this case and never, e.g., a 404 "chunk not found" (which is also a
/// <see cref="DataIntegrityException"/> but is permanent).
/// </summary>
public sealed class DownloadIntegrityException : DataIntegrityException
{
    public DownloadIntegrityException(string message, string affectedResource)
        : base(message, affectedResource)
    {
    }
}

/// <summary>
/// Exception thrown when security policy is violated.
/// </summary>
public class SecurityPolicyException : Exception
{
    public SecurityPolicyType PolicyType { get; }

    public SecurityPolicyException(string message, SecurityPolicyType policyType) : base(message)
    {
        PolicyType = policyType;
    }

    public SecurityPolicyException(string message, SecurityPolicyType policyType, Exception innerException) 
        : base(message, innerException)
    {
        PolicyType = policyType;
    }
}

public enum SecurityPolicyType
{
    RateLimitExceeded,
    AccountLocked,
    InvalidCredentials,
    InvalidBlobName,
    TamperingDetected,
    WeakPassword
}

/// <summary>
/// Exception thrown when an invalid password is provided for the encrypted database.
/// </summary>
public class InvalidPasswordException : Exception
{
    public InvalidPasswordException(string message) : base(message)
    {
    }

    public InvalidPasswordException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when Azure rejects an operation because of authentication or
/// authorization failure (HTTP 401 / 403, or an <c>AuthenticationFailedException</c>
/// from the Azure SDK). Carries the original Azure error for diagnostics so callers
/// can decide whether to invalidate cached credentials and prompt the user to re-auth.
/// </summary>
public class AzureAuthenticationException : Exception
{
    /// <summary>HTTP status code reported by Azure, if available (0 when not HTTP-based).</summary>
    public int Status { get; }

    /// <summary>Azure error code (e.g. "AuthenticationFailed"), if available.</summary>
    public string? ErrorCode { get; }

    public AzureAuthenticationException(string message, int status, string? errorCode, Exception innerException)
        : base(message, innerException)
    {
        Status = status;
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Exception thrown when a hash collision is detected during deduplication verification.
/// This is an extremely rare event (SHA-256 collision probability is 2^-128) and likely
/// indicates data corruption, a bug, or an intentional attack rather than a true collision.
/// </summary>
public class HashCollisionException : Exception
{
    /// <summary>
    /// The hash value that matched but with different data.
    /// </summary>
    public string ChunkHash { get; }

    /// <summary>
    /// Size of the expected data in bytes.
    /// </summary>
    public long ExpectedSize { get; }

    /// <summary>
    /// Size of the stored data in bytes.
    /// </summary>
    public long StoredSize { get; }

    public HashCollisionException(string chunkHash, long expectedSize, long storedSize)
        : base($"CRITICAL: Hash collision detected for chunk {chunkHash}. " +
               $"Expected {expectedSize} bytes but stored chunk has {storedSize} bytes. " +
               "This may indicate data corruption or tampering.")
    {
        ChunkHash = chunkHash;
        ExpectedSize = expectedSize;
        StoredSize = storedSize;
    }

    public HashCollisionException(string chunkHash, string details)
        : base($"CRITICAL: Hash collision detected for chunk {chunkHash}. {details}")
    {
        ChunkHash = chunkHash;
    }
}

/// <summary>
/// B44: thrown when an operation against the local SQLite/SQLCipher
/// catalog file fails because the file itself is corrupted on disk
/// (SQLite error 11, <c>SQLITE_CORRUPT</c>, "database disk image is
/// malformed", or SQLite error 26, <c>SQLITE_NOTADB</c>, raised on a
/// page fetch rather than at PRAGMA-key time).
///
/// <para>
/// Distinct from <see cref="DataIntegrityException"/> on purpose:
/// <see cref="DataIntegrityException"/> describes the user's backed-up
/// files failing verification against Azure storage, whereas this
/// exception describes the local catalog file itself being unreadable.
/// The repair paths are completely different -- the user-visible Data
/// Integrity feature cannot fix this; the user must run the
/// "Verify Database File" diagnostic on the Storage Health tab and
/// follow its guidance (typically: restore the catalog from a backup
/// or rebuild it from Azure metadata).
/// </para>
/// </summary>
public class DatabaseFileCorruptException : Exception
{
    /// <summary>
    /// Underlying SQLite error code (e.g. 11 for SQLITE_CORRUPT,
    /// 26 for SQLITE_NOTADB) when the cause was a SqliteException;
    /// 0 otherwise.
    /// </summary>
    public int SqliteErrorCode { get; }

    public DatabaseFileCorruptException(string message, int sqliteErrorCode, Exception innerException)
        : base(message, innerException)
    {
        SqliteErrorCode = sqliteErrorCode;
    }
}

/// <summary>
/// Base type for provider-neutral object-storage failures surfaced by an
/// <see cref="SecureCloudBackup.Core.Services.IBlobStorageService"/>
/// implementation. Provider adapters (Azure today, others later) translate their
/// cloud SDK's exceptions into this hierarchy at the service boundary so
/// consumers -- retry classification, error reporting -- never depend on a
/// specific SDK's exception types.
/// </summary>
public class StorageException : Exception
{
    public StorageException(string message) : base(message)
    {
    }

    public StorageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// A transient object-storage failure (HTTP 408/429/500/502/503/504, throttling,
/// or a network blip) that is worth retrying. Provider adapters translate their
/// SDK's transient errors into this type at the service boundary so retry
/// policies stay provider-agnostic.
/// </summary>
public sealed class TransientStorageException : StorageException
{
    /// <summary>Provider HTTP status code when available; 0 otherwise.</summary>
    public int Status { get; }

    public TransientStorageException(string message, int status, Exception innerException)
        : base(message, innerException)
    {
        Status = status;
    }
}
