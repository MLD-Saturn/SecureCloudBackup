using System.Text.Json.Serialization;

namespace SecureCloudBackup.Migration;

/// <summary>
/// The migration request the launching app streams to the helper over stdin
/// (as a single JSON line). The password is passed this way -- never via argv
/// or environment variables, which leak through process listings.
///
/// <para>
/// The password is a <c>char[]</c> (not a <c>string</c>) so the helper can ZERO
/// it after deriving the key. It is read by <see cref="CharArrayJsonConverter"/>
/// straight into a char buffer, and the raw stdin buffer is cleared by the
/// caller, so no un-zeroable <c>string</c> copy of the secret is created.
/// </para>
/// </summary>
internal sealed class MigrationRequest
{
    /// <summary>Absolute path to the legacy SQLCipher database file.</summary>
    public string LegacyDatabasePath { get; set; } = string.Empty;

    /// <summary>
    /// Base64 of the 16-byte salt from the legacy database's <c>.salt</c> sidecar,
    /// used (with the password) to derive the SQLCipher unlock key via Argon2id.
    /// </summary>
    public string LegacySaltBase64 { get; set; } = string.Empty;

    /// <summary>The user's password, as a zeroable char buffer.</summary>
    [JsonConverter(typeof(CharArrayJsonConverter))]
    public char[] Password { get; set; } = [];

    /// <summary>
    /// Absolute path the helper writes the new AES-256-GCM encrypted snapshot to.
    /// The launching app verifies and then atomically swaps this over the original.
    /// </summary>
    public string OutputSnapshotPath { get; set; } = string.Empty;

    /// <summary>Zeroes the password buffer. Call once the password is no longer needed.</summary>
    public void ClearPassword()
    {
        if (Password.Length > 0)
            Array.Clear(Password);
    }
}
