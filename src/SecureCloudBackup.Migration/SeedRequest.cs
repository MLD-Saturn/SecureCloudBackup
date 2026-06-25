using System.Text.Json.Serialization;

namespace SecureCloudBackup.Migration;

/// <summary>
/// The request the test harness streams to the helper's <c>seed</c> subcommand
/// over stdin (as a single JSON line). It instructs the helper to create a real
/// legacy SQLCipher catalog on disk so the cross-process migration tests have a
/// genuine source to migrate -- the test process itself cannot run the SQLCipher
/// engine (it loads <c>e_sqlite3</c> via Core, which disables SQLCipher).
///
/// <para>
/// Like <see cref="MigrationRequest"/>, the password is delivered ONLY on stdin
/// (never argv/env) and is a zeroable <c>char[]</c> so the helper can clear it
/// after deriving the SQLCipher key.
/// </para>
/// </summary>
internal sealed class SeedRequest
{
    /// <summary>Absolute path the helper writes the new SQLCipher database file to.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>
    /// Base64 of the 16-byte salt to write to the database's <c>.salt</c> sidecar
    /// and to derive the SQLCipher key from (with the password) via Argon2id.
    /// </summary>
    public string SaltBase64 { get; set; } = string.Empty;

    /// <summary>The password the seeded catalog is protected with, as a zeroable char buffer.</summary>
    [JsonConverter(typeof(CharArrayJsonConverter))]
    public char[] Password { get; set; } = [];

    /// <summary>
    /// How many rows to write into the <c>files</c> table. Zero produces a valid
    /// schema-only catalog (the single <c>config</c> row is always written).
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>Zeroes the password buffer. Call once the password is no longer needed.</summary>
    public void ClearPassword()
    {
        if (Password.Length > 0)
            Array.Clear(Password);
    }
}
