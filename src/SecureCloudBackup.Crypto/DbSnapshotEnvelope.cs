using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace SecureCloudBackup.Crypto;

/// <summary>
/// Encrypts and decrypts a serialized SQLite database image (a plain
/// <c>byte[]</c> produced by <c>sqlite3_serialize</c>) under a password-derived
/// key, producing the application-level encrypted database snapshot that
/// replaces SQLCipher's transparent page encryption.
///
/// <para>
/// <b>On-disk layout</b> (all multi-byte integers little-endian):
/// </para>
/// <code>
/// [ magic "AZDB" (4) | version (1) | salt (16) | nonce (12) | ciphertext (N) | tag (16) | crc32 (4) ]
/// </code>
///
/// <list type="bullet">
///   <item><b>magic</b> distinguishes this format from the chunk-encryption
///   envelope (<c>AZBK</c>) so the two can never be confused.</item>
///   <item><b>salt</b> is embedded so the snapshot is self-describing: the
///   unlock key is <c>Argon2id(password, salt)</c> with the canonical
///   <see cref="KdfParameters"/> cost. This salt is the snapshot's OWN salt
///   domain, independent of the on-disk <c>.salt</c> sidecar and the in-DB
///   <c>config.password_salt</c>.</item>
///   <item><b>nonce</b> is a fresh 96-bit random value per encryption.</item>
///   <item><b>AES-256-GCM</b> authenticates the ciphertext; the 16-byte
///   <b>tag</b> makes a wrong key or any tampering fail loudly with
///   <see cref="DbSnapshotException"/> rather than silently returning
///   garbage (the exact failure mode that motivated leaving SQLCipher).</item>
///   <item><b>crc32</b> over <c>magic..tag</c> detects accidental on-disk
///   corruption before the (more expensive) GCM check and is the only field
///   not covered by the GCM tag.</item>
/// </list>
///
/// <para>
/// Plaintext (the serialized image) lives only in memory; the only artifact
/// written to disk is the encrypted output of <see cref="Encrypt"/>.
/// </para>
/// </summary>
public static class DbSnapshotEnvelope
{
    private static readonly byte[] Magic = "AZDB"u8.ToArray();

    private const int MagicSize = 4;
    private const int VersionSize = 1;
    private const int SaltSize = KdfParameters.SaltSize;     // 16
    private const int NonceSize = 12;                        // AES-GCM 96-bit nonce
    private const int TagSize = 16;                          // AES-GCM 128-bit tag
    private const int Crc32Size = 4;

    /// <summary>The current snapshot format version written by <see cref="Encrypt"/>.</summary>
    public const byte CurrentVersion = 1;

    private const int HeaderSize = MagicSize + VersionSize + SaltSize + NonceSize; // 33
    private const int FixedOverhead = HeaderSize + TagSize + Crc32Size;            // 53

    /// <summary>
    /// Total fixed byte overhead added around the serialized image:
    /// magic(4) + version(1) + salt(16) + nonce(12) + tag(16) + crc32(4) = 53.
    /// </summary>
    public const int Overhead = FixedOverhead;

    /// <summary>
    /// Encrypts <paramref name="serializedImage"/> (a serialized SQLite image)
    /// into a self-describing snapshot blob keyed by <paramref name="password"/>.
    /// A fresh random salt and nonce are generated each call, and the Argon2id key
    /// is derived (and zeroed) internally.
    /// </summary>
    /// <remarks>
    /// This convenience overload runs the full Argon2id KDF every call. Callers
    /// that persist repeatedly within a session (e.g. the in-memory snapshot
    /// backend's checkpoint loop) should instead derive the key once via
    /// <see cref="Argon2idDeriver"/> and use <see cref="Encrypt(ReadOnlySpan{byte}, byte[], byte[])"/>
    /// so they do not pay the 64 MB KDF cost on every write.
    /// </remarks>
    /// <param name="serializedImage">The plain SQLite image bytes to protect.</param>
    /// <param name="password">The user's password; the key is derived via Argon2id.</param>
    /// <returns>The encrypted snapshot blob, safe to write to disk.</returns>
    public static byte[] Encrypt(ReadOnlySpan<byte> serializedImage, ReadOnlySpan<char> password)
    {
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty.", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Argon2idDeriver.DeriveKey(password, salt, "database snapshot key");
        try
        {
            return Encrypt(serializedImage, key, salt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Encrypts <paramref name="serializedImage"/> using a pre-derived 32-byte
    /// <paramref name="key"/> and its <paramref name="salt"/> (which is embedded in
    /// the output so the matching <see cref="Decrypt(ReadOnlySpan{byte}, ReadOnlySpan{char})"/>
    /// can re-derive the same key). A fresh random nonce is generated each call --
    /// the key may be reused across writes but the nonce MUST NOT, so this method
    /// always allocates a new one.
    /// </summary>
    /// <param name="serializedImage">The plain SQLite image bytes to protect.</param>
    /// <param name="key">The 32-byte AES-256 key (e.g. from <see cref="Argon2idDeriver.DeriveKey"/>).</param>
    /// <param name="salt">The <see cref="KdfParameters.SaltSize"/>-byte salt that produced <paramref name="key"/>.</param>
    public static byte[] Encrypt(ReadOnlySpan<byte> serializedImage, byte[] key, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(salt);
        if (key.Length != KdfParameters.DerivedKeySize)
            throw new ArgumentException($"Key must be {KdfParameters.DerivedKeySize} bytes.", nameof(key));
        if (salt.Length != SaltSize)
            throw new ArgumentException($"Salt must be {SaltSize} bytes.", nameof(salt));

        // Fresh nonce per write -- never reuse a (key, nonce) pair under GCM.
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);

        var ciphertext = new byte[serializedImage.Length];
        var tag = new byte[TagSize];
        using (var gcm = new AesGcm(key, TagSize))
        {
            gcm.Encrypt(nonce, serializedImage, ciphertext, tag);
        }

        var output = new byte[FixedOverhead + serializedImage.Length];
        var pos = 0;
        Magic.CopyTo(output.AsSpan(pos)); pos += MagicSize;
        output[pos] = CurrentVersion; pos += VersionSize;
        salt.CopyTo(output.AsSpan(pos)); pos += SaltSize;
        nonce.CopyTo(output.AsSpan(pos)); pos += NonceSize;
        ciphertext.CopyTo(output.AsSpan(pos)); pos += ciphertext.Length;
        tag.CopyTo(output.AsSpan(pos)); pos += TagSize;

        // CRC32 over everything written so far (magic..tag), excluding the
        // 4-byte CRC slot itself.
        var crc = Crc32.HashToUInt32(output.AsSpan(0, pos));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(pos), crc);

        return output;
    }

    /// <summary>
    /// Decrypts a snapshot blob produced by <see cref="Encrypt"/> back into the
    /// serialized SQLite image, using <paramref name="password"/>.
    /// </summary>
    /// <exception cref="DbSnapshotException">
    /// The blob is malformed, the CRC fails, the version is unsupported, or the
    /// password is wrong / the data was tampered with (GCM authentication fail).
    /// </exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> snapshot, ReadOnlySpan<char> password)
        => DecryptAndExtractKey(snapshot, password, out _, out _);

    /// <summary>
    /// Decrypts a snapshot and ALSO returns the derived <paramref name="key"/> and
    /// its <paramref name="salt"/> so the caller can keep them to re-encrypt
    /// subsequent snapshots within the same session WITHOUT paying the Argon2id
    /// cost again (passing them to <see cref="Encrypt(ReadOnlySpan{byte}, byte[], byte[])"/>).
    /// The caller OWNS <paramref name="key"/> and must zero it when done.
    /// </summary>
    /// <exception cref="DbSnapshotException">
    /// The blob is malformed, the CRC fails, the version is unsupported, or the
    /// password is wrong / the data was tampered with (GCM authentication fail).
    /// </exception>
    public static byte[] DecryptAndExtractKey(
        ReadOnlySpan<byte> snapshot, ReadOnlySpan<char> password, out byte[] key, out byte[] salt)
    {
        if (password.IsEmpty)
            throw new ArgumentException("Password cannot be empty.", nameof(password));
        if (snapshot.Length < FixedOverhead)
            throw new DbSnapshotException("Snapshot is too small to be valid.");

        if (!snapshot[..MagicSize].SequenceEqual(Magic))
            throw new DbSnapshotException("Snapshot magic header mismatch (not an AZDB snapshot).");

        var pos = MagicSize;
        var version = snapshot[pos]; pos += VersionSize;
        if (version != CurrentVersion)
            throw new DbSnapshotException($"Unsupported snapshot version {version} (expected {CurrentVersion}).");

        // Verify CRC32 (over magic..tag) before doing crypto work.
        var crcOffset = snapshot.Length - Crc32Size;
        var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(snapshot[crcOffset..]);
        var actualCrc = Crc32.HashToUInt32(snapshot[..crcOffset]);
        if (expectedCrc != actualCrc)
            throw new DbSnapshotException("Snapshot CRC mismatch (file is corrupted).");

        var saltLocal = snapshot.Slice(pos, SaltSize).ToArray(); pos += SaltSize;
        var nonce = snapshot.Slice(pos, NonceSize); pos += NonceSize;
        var cipherLength = crcOffset - TagSize - pos;
        if (cipherLength < 0)
            throw new DbSnapshotException("Snapshot ciphertext length is invalid.");
        var ciphertext = snapshot.Slice(pos, cipherLength);
        var tag = snapshot.Slice(pos + cipherLength, TagSize);

        var keyLocal = Argon2idDeriver.DeriveKey(password, saltLocal, "database snapshot key");
        try
        {
            var plaintext = new byte[cipherLength];
            using (var gcm = new AesGcm(keyLocal, TagSize))
            {
                gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            }
            // Success: hand the key + salt to the caller (who now owns the key).
            key = keyLocal;
            salt = saltLocal;
            keyLocal = null!; // prevent the finally from zeroing the caller's key
            return plaintext;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new DbSnapshotException(
                "Snapshot authentication failed: wrong password or the snapshot was tampered with.", ex);
        }
        finally
        {
            if (keyLocal != null)
                CryptographicOperations.ZeroMemory(keyLocal);
        }
    }

    /// <summary>
    /// True if <paramref name="data"/> begins with the AZDB magic header, used
    /// to distinguish a new-format snapshot from a legacy SQLCipher database
    /// file without attempting decryption.
    /// </summary>
    public static bool HasMagic(ReadOnlySpan<byte> data)
        => data.Length >= MagicSize && data[..MagicSize].SequenceEqual(Magic);
}
