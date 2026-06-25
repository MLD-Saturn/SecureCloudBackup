namespace SecureCloudBackup.Migration;

/// <summary>
/// Process exit codes for the migration helper. The launching app interprets
/// these to decide whether the migration succeeded and, if not, why.
/// </summary>
internal enum MigrationExitCode
{
    /// <summary>The legacy catalog was migrated and the new snapshot written and verified.</summary>
    Success = 0,

    /// <summary>The stdin request was missing, malformed, or had invalid fields.</summary>
    BadRequest = 2,

    /// <summary>The legacy database file or its salt sidecar was not found.</summary>
    SourceNotFound = 3,

    /// <summary>The password did not unlock the legacy SQLCipher database.</summary>
    InvalidPassword = 4,

    /// <summary>The legacy database could be opened but reading/copying it failed.</summary>
    ReadFailed = 5,

    /// <summary>The new encrypted snapshot could not be written or failed verification.</summary>
    WriteFailed = 6,

    /// <summary>An unexpected error occurred.</summary>
    Unexpected = 1,
}
