namespace AzureBackup.Migration;

/// <summary>
/// The migration request the launching app streams to the helper over stdin
/// (as a single JSON line). The password is passed this way -- never via argv
/// or environment variables, which leak through process listings.
/// </summary>
/// <param name="LegacyDatabasePath">Absolute path to the legacy SQLCipher database file.</param>
/// <param name="LegacySaltBase64">
/// Base64 of the 16-byte salt from the legacy database's <c>.salt</c> sidecar,
/// used (with the password) to derive the SQLCipher unlock key via Argon2id.
/// </param>
/// <param name="Password">The user's password.</param>
/// <param name="OutputSnapshotPath">
/// Absolute path the helper writes the new AES-256-GCM encrypted snapshot to.
/// The launching app verifies and then atomically swaps this over the original.
/// </param>
internal sealed record MigrationRequest(
    string LegacyDatabasePath,
    string LegacySaltBase64,
    string Password,
    string OutputSnapshotPath);
