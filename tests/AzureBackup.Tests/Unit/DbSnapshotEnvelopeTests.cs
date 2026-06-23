using System.Security.Cryptography;
using AzureBackup.Crypto;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for <see cref="DbSnapshotEnvelope"/>, the application-level
/// encrypted database snapshot format that replaces SQLCipher. Verifies the
/// round-trip, that a wrong password or any tampering fails loudly (never a
/// silent garbage decrypt), and that the format is self-describing.
/// </summary>
public class DbSnapshotEnvelopeTests
{
    private static byte[] SampleImage(int size = 4096)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        // Make it look a little like a SQLite image header for realism.
        var header = "SQLite format 3\0"u8.ToArray();
        header.CopyTo(bytes, 0);
        return bytes;
    }

    [Fact]
    public void Encrypt_ThenDecrypt_WithSamePassword_RoundTrips()
    {
        var image = SampleImage();
        const string password = "correct horse battery staple";

        var snapshot = DbSnapshotEnvelope.Encrypt(image, password);
        var recovered = DbSnapshotEnvelope.Decrypt(snapshot, password);

        Assert.Equal(image, recovered);
    }

    [Fact]
    public void Encrypt_ProducesBlobLargerThanPlaintextByOverhead()
    {
        var image = SampleImage(1000);

        var snapshot = DbSnapshotEnvelope.Encrypt(image, "pw");

        Assert.Equal(image.Length + DbSnapshotEnvelope.Overhead, snapshot.Length);
    }

    [Fact]
    public void Encrypt_ProducesAzdbMagicHeader()
    {
        var snapshot = DbSnapshotEnvelope.Encrypt(SampleImage(64), "pw");

        Assert.True(DbSnapshotEnvelope.HasMagic(snapshot));
    }

    [Fact]
    public void Encrypt_TwoCallsSameInput_ProduceDifferentCiphertext()
    {
        var image = SampleImage(256);
        const string password = "pw";

        var a = DbSnapshotEnvelope.Encrypt(image, password);
        var b = DbSnapshotEnvelope.Encrypt(image, password);

        // Fresh random salt + nonce per call => different bytes.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Decrypt_WithWrongPassword_ThrowsDbSnapshotException()
    {
        var snapshot = DbSnapshotEnvelope.Encrypt(SampleImage(512), "right-password");

        Assert.Throws<DbSnapshotException>(
            () => DbSnapshotEnvelope.Decrypt(snapshot, "wrong-password"));
    }

    [Fact]
    public void Decrypt_WithTamperedCiphertext_ThrowsDbSnapshotException()
    {
        var snapshot = DbSnapshotEnvelope.Encrypt(SampleImage(512), "pw");
        // Flip a byte well inside the ciphertext region (past the 33-byte header).
        snapshot[80] ^= 0xFF;

        Assert.Throws<DbSnapshotException>(
            () => DbSnapshotEnvelope.Decrypt(snapshot, "pw"));
    }

    [Fact]
    public void Decrypt_WithCorruptedTrailingCrc_ThrowsDbSnapshotException()
    {
        var snapshot = DbSnapshotEnvelope.Encrypt(SampleImage(128), "pw");
        // Corrupt the last byte (inside the CRC32 slot) without touching ciphertext.
        snapshot[^1] ^= 0xFF;

        Assert.Throws<DbSnapshotException>(
            () => DbSnapshotEnvelope.Decrypt(snapshot, "pw"));
    }

    [Fact]
    public void Decrypt_WithNonAzdbMagic_ThrowsDbSnapshotException()
    {
        var notASnapshot = new byte[DbSnapshotEnvelope.Overhead + 10];
        RandomNumberGenerator.Fill(notASnapshot);

        Assert.Throws<DbSnapshotException>(
            () => DbSnapshotEnvelope.Decrypt(notASnapshot, "pw"));
    }

    [Fact]
    public void Decrypt_WithTooShortBlob_ThrowsDbSnapshotException()
    {
        var tooShort = new byte[DbSnapshotEnvelope.Overhead - 1];

        Assert.Throws<DbSnapshotException>(
            () => DbSnapshotEnvelope.Decrypt(tooShort, "pw"));
    }

    [Fact]
    public void HasMagic_OnPlainSqliteHeader_ReturnsFalse()
    {
        var plainSqlite = "SQLite format 3\0"u8.ToArray();

        Assert.False(DbSnapshotEnvelope.HasMagic(plainSqlite));
    }

    [Fact]
    public void Encrypt_WithEmptyPassword_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => DbSnapshotEnvelope.Encrypt(SampleImage(16), string.Empty));
    }

    [Fact]
    public void Encrypt_ThenDecrypt_EmptyImage_RoundTripsToEmpty()
    {
        var snapshot = DbSnapshotEnvelope.Encrypt(ReadOnlySpan<byte>.Empty, "pw");
        var recovered = DbSnapshotEnvelope.Decrypt(snapshot, "pw");

        Assert.Empty(recovered);
    }

    [Fact]
    public void DecryptAndExtractKey_ReturnsKeyAndSalt_UsableForReEncrypt()
    {
        var image = SampleImage(256);
        var snapshot = DbSnapshotEnvelope.Encrypt(image, "pw");

        var recovered = DbSnapshotEnvelope.DecryptAndExtractKey(snapshot, "pw", out var key, out var salt);

        Assert.Equal(image, recovered);
        Assert.Equal(32, key.Length);
        Assert.Equal(KdfParameters.SaltSize, salt.Length);

        // The extracted key+salt must round-trip a NEW image with the cheap overload.
        var image2 = SampleImage(512);
        var snapshot2 = DbSnapshotEnvelope.Encrypt(image2, key, salt);
        var recovered2 = DbSnapshotEnvelope.Decrypt(snapshot2, "pw");
        Assert.Equal(image2, recovered2);
    }

    [Fact]
    public void Encrypt_WithKeyAndSalt_TwoCalls_UseDifferentNonces()
    {
        var image = SampleImage(256);
        var key = new byte[32];
        var salt = new byte[KdfParameters.SaltSize];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);

        var a = DbSnapshotEnvelope.Encrypt(image, key, salt);
        var b = DbSnapshotEnvelope.Encrypt(image, key, salt);

        // Same key + salt but a fresh nonce per write => different ciphertext.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Encrypt_WithWrongKeyLength_ThrowsArgumentException()
    {
        var salt = new byte[KdfParameters.SaltSize];

        Assert.Throws<ArgumentException>(
            () => DbSnapshotEnvelope.Encrypt(SampleImage(16), new byte[16], salt));
    }

    [Fact]
    public void Encrypt_WithWrongSaltLength_ThrowsArgumentException()
    {
        var key = new byte[32];

        Assert.Throws<ArgumentException>(
            () => DbSnapshotEnvelope.Encrypt(SampleImage(16), key, new byte[8]));
    }
}
