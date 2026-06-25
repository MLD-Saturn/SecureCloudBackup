namespace SecureCloudBackup.Core.Services.Backends;

/// <summary>
/// Thrown when the automatic legacy-SQLCipher-to-snapshot migration fails. When
/// this is thrown the ORIGINAL catalog is left intact (the migration is
/// all-or-nothing and reversible), so the operation can be retried on the next
/// unlock or the error surfaced to the user.
/// </summary>
internal sealed class LegacyMigrationException : Exception
{
    public LegacyMigrationException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
