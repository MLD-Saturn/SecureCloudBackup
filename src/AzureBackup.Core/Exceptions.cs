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
/// Exception thrown when backup/restore operations fail.
/// </summary>
public class BackupOperationException : Exception
{
    public BackupOperationType OperationType { get; }
    public string? FilePath { get; }

    public BackupOperationException(string message, BackupOperationType operationType) : base(message)
    {
        OperationType = operationType;
    }

    public BackupOperationException(string message, BackupOperationType operationType, string filePath) 
        : base(message)
    {
        OperationType = operationType;
        FilePath = filePath;
    }

    public BackupOperationException(string message, BackupOperationType operationType, Exception innerException) 
        : base(message, innerException)
    {
        OperationType = operationType;
    }
}

public enum BackupOperationType
{
    Chunking,
    Encryption,
    Upload,
    Download,
    Restore,
    MetadataSync
}
