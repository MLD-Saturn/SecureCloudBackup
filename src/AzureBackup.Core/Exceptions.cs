namespace AzureBackup.Core;

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
