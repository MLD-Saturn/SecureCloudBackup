namespace AzureBackup.Crypto;

/// <summary>
/// Thrown when an encrypted database snapshot cannot be read: malformed header,
/// unsupported version, CRC corruption, or authentication failure (wrong
/// password or tampering). Distinct from a generic exception so callers can
/// surface a precise "wrong password vs. corrupted file" message, and never
/// silently proceed on a bad key.
/// </summary>
public sealed class DbSnapshotException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public DbSnapshotException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public DbSnapshotException(string message, Exception innerException)
        : base(message, innerException) { }
}
