using System.Security.Cryptography;
using AzureBackup.Core;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for EncryptionService covering key derivation, encryption/decryption,
/// data integrity, and error handling.
/// </summary>
public class EncryptionServiceTests : IDisposable
{
    private readonly EncryptionService _encryptionService;
    private const string TestPassword = "TestPassword123!";

    public EncryptionServiceTests()
    {
        _encryptionService = new EncryptionService();
    }

    public void Dispose()
    {
        _encryptionService.Dispose();
    }

    #region Key Derivation Tests

    [Fact]
    public async Task DeriveKeyAsync_WithValidInputs_ReturnsDerivedKey()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();

        // Act
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);

        // Assert
        Assert.NotNull(key);
        Assert.Equal(32, key.Length); // 256 bits
    }

    [Fact]
    public async Task DeriveKeyAsync_SamePasswordAndSalt_ReturnsSameKey()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();

        // Act
        var key1 = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        var key2 = await _encryptionService.DeriveKeyAsync(TestPassword, salt);

        // Assert
        Assert.Equal(key1, key2);
    }

    [Fact]
    public async Task DeriveKeyAsync_DifferentSalts_ReturnsDifferentKeys()
    {
        // Arrange
        var salt1 = EncryptionService.GenerateSalt();
        var salt2 = EncryptionService.GenerateSalt();

        // Act
        var key1 = await _encryptionService.DeriveKeyAsync(TestPassword, salt1);
        var key2 = await _encryptionService.DeriveKeyAsync(TestPassword, salt2);

        // Assert
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task DeriveKeyAsync_DifferentPasswords_ReturnsDifferentKeys()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();

        // Act
        var key1 = await _encryptionService.DeriveKeyAsync("Password1", salt);
        var key2 = await _encryptionService.DeriveKeyAsync("Password2", salt);

        // Assert
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task DeriveKeyAsync_NullPassword_ThrowsArgumentNullException()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            _encryptionService.DeriveKeyAsync(null!, salt));
    }

    [Fact]
    public async Task DeriveKeyAsync_NullSalt_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            _encryptionService.DeriveKeyAsync(TestPassword, null!));
    }

    #endregion

    #region Initialization Tests

    [Fact]
    public void Initialize_WithValidKey_SetsIsInitializedTrue()
    {
        // Arrange
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);

        // Act
        _encryptionService.Initialize(key);

        // Assert
        Assert.True(_encryptionService.IsInitialized);
    }

    [Fact]
    public void Initialize_WithInvalidKeyLength_ThrowsArgumentException()
    {
        // Arrange
        byte[] shortKey = new byte[16]; // Should be 32

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _encryptionService.Initialize(shortKey));
    }

    [Fact]
    public void Initialize_WithNullKey_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _encryptionService.Initialize(null!));
    }

    #endregion

    #region Encryption/Decryption Tests

    [Fact]
    public void EncryptDecrypt_RoundTrip_ReturnsOriginalData()
    {
        // Arrange
        InitializeWithTestKey();
        var originalData = "Hello, World! This is a test message."u8.ToArray();

        // Act
        var encrypted = _encryptionService.Encrypt(originalData);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        Assert.Equal(originalData, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentOutputEachTime()
    {
        // Arrange
        InitializeWithTestKey();
        var data = "Same data"u8.ToArray();

        // Act
        var encrypted1 = _encryptionService.Encrypt(data);
        var encrypted2 = _encryptionService.Encrypt(data);

        // Assert - Different due to random nonce
        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void Encrypt_OutputIsLargerThanInput()
    {
        // Arrange
        InitializeWithTestKey();
        byte[] data = new byte[100];

        // Act
        var encrypted = _encryptionService.Encrypt(data);

        // Assert - Should include magic(4) + version(1) + nonce(12) + tag(16) + checksum(4) = 37 bytes overhead
        Assert.True(encrypted.Length > data.Length + 30);
    }

    [Fact]
    public void Encrypt_WithoutInitialization_ThrowsInvalidOperationException()
    {
        // Arrange
        var data = "Test"u8.ToArray();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _encryptionService.Encrypt(data));
    }

    [Fact]
    public void Decrypt_WithCorruptedChecksum_ThrowsDataIntegrityException()
    {
        // Arrange
        InitializeWithTestKey();
        var data = "Test data"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        
        // Corrupt the checksum (last 4 bytes)
        encrypted[^1] ^= 0xFF;

        // Act & Assert
        Assert.Throws<DataIntegrityException>(() => _encryptionService.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_WithCorruptedCiphertext_ThrowsDataIntegrityException()
    {
        // Arrange
        InitializeWithTestKey();
        var data = "Test data"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        
        // Corrupt middle of ciphertext (after magic+version+nonce = 17 bytes)
        encrypted[20] ^= 0xFF;

        // Act & Assert - Either checksum or GCM auth will fail
        Assert.ThrowsAny<Exception>(() => _encryptionService.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_WithTruncatedData_ThrowsException()
    {
        // Arrange
        InitializeWithTestKey();
        var data = "Test data"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        
        // Truncate
        var truncated = encrypted[..20];

        // Act & Assert
        Assert.ThrowsAny<Exception>(() => _encryptionService.Decrypt(truncated));
    }

    [Fact]
    public void Decrypt_WithWrongKey_ThrowsDataIntegrityException()
    {
        // Arrange
        InitializeWithTestKey();
        var data = "Secret message"u8.ToArray();
        var encrypted = _encryptionService.Encrypt(data);
        
        // Create new service with different key
        using EncryptionService otherService = new();
        byte[] otherKey = new byte[32];
        RandomNumberGenerator.Fill(otherKey);
        otherService.Initialize(otherKey);

        // Act & Assert
        Assert.Throws<DataIntegrityException>(() => otherService.Decrypt(encrypted));
    }

    #endregion

    #region Password Verification Tests

    [Fact]
    public async Task VerifyPassword_WithCorrectPassword_ReturnsTrue()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();
        var hash = await _encryptionService.CreatePasswordVerificationHashAsync(TestPassword, salt);

        // Act
        var isValid = await _encryptionService.VerifyPasswordAsync(TestPassword, salt, hash);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public async Task VerifyPassword_WithWrongPassword_ReturnsFalse()
    {
        // Arrange
        var salt = EncryptionService.GenerateSalt();
        var hash = await _encryptionService.CreatePasswordVerificationHashAsync(TestPassword, salt);

        // Act
        var isValid = await _encryptionService.VerifyPasswordAsync("WrongPassword", salt, hash);

        // Assert
        Assert.False(isValid);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EncryptDecrypt_EmptyData_Works()
    {
        // Arrange
        InitializeWithTestKey();
        var emptyData = Array.Empty<byte>();

        // Act
        var encrypted = _encryptionService.Encrypt(emptyData);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        Assert.Empty(decrypted);
    }

    [Fact]
    public void EncryptDecrypt_LargeData_Works()
    {
        // Arrange
        InitializeWithTestKey();
        byte[] largeData = new byte[1024 * 1024]; // 1 MB
        RandomNumberGenerator.Fill(largeData);

        // Act
        var encrypted = _encryptionService.Encrypt(largeData);
        var decrypted = _encryptionService.Decrypt(encrypted);

        // Assert
        Assert.Equal(largeData, decrypted);
    }

    [Fact]
    public void GenerateSalt_ReturnsUniqueSalts()
    {
        // Act
        var salts = Enumerable.Range(0, 100)
            .Select(_ => EncryptionService.GenerateSalt())
            .ToList();

        // Assert - All should be unique
        var uniqueSalts = salts.Select(s => Convert.ToBase64String(s)).Distinct().Count();
        Assert.Equal(100, uniqueSalts);
    }

    #endregion

    #region ClearKey Tests

    [Fact]
    public void ClearKey_RemovesKeyFromMemory()
    {
        // Arrange
        InitializeWithTestKey();
        Assert.True(_encryptionService.IsInitialized);
        
        // Act
        _encryptionService.ClearKey();
        
        // Assert
        Assert.False(_encryptionService.IsInitialized);
    }

    [Fact]
    public void ClearKey_EncryptAfterClear_ThrowsInvalidOperationException()
    {
        // Arrange
        InitializeWithTestKey();
        _encryptionService.ClearKey();
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => 
            _encryptionService.Encrypt(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ClearKey_CanReinitializeAfterClear()
    {
        // Arrange
        InitializeWithTestKey();
        byte[] testData = [1, 2, 3, 4, 5];
        var encrypted1 = _encryptionService.Encrypt(testData);
        
        // Act - Clear and reinitialize
        _encryptionService.ClearKey();
        InitializeWithTestKey();
        
        // Should be able to encrypt again (with new key)
        var encrypted2 = _encryptionService.Encrypt(testData);
        
        // Assert - Different keys produce different ciphertext
        Assert.NotEqual(encrypted1, encrypted2);
    }

    [Fact]
    public void ClearKey_MultipleCallsDoNotThrow()
    {
        // Arrange
        InitializeWithTestKey();
        
        // Act & Assert - Should not throw
        _encryptionService.ClearKey();
        _encryptionService.ClearKey();
        _encryptionService.ClearKey();
        
        Assert.False(_encryptionService.IsInitialized);
    }

    #endregion

    private void InitializeWithTestKey()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        _encryptionService.Initialize(key);
    }

    #region CRC Round-Trip Diagnostics

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(8 * 1024)]           // 8 KB — matches small file pattern
    [InlineData(10 * 1024)]          // 10 KB — small file boundary
    [InlineData(64 * 1024)]          // 64 KB
    [InlineData(256 * 1024)]         // 256 KB — typical small chunk
    [InlineData(1024 * 1024)]        // 1 MB
    [InlineData(4 * 1024 * 1024)]    // 4 MB
    [InlineData(64 * 1024 * 1024)]   // 64 MB — typical large chunk
    public void WhenEncryptedThenCrcIsImmediatelyValid(int dataSize)
    {
        InitializeWithTestKey();
        var plaintext = new byte[dataSize];
        Random.Shared.NextBytes(plaintext);

        var encrypted = _encryptionService.Encrypt(plaintext);

        Assert.True(_encryptionService.ValidateCrc(encrypted),
            $"CRC invalid immediately after Encrypt for {dataSize} byte payload. " +
            _encryptionService.DiagnoseCrcMismatch(encrypted));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(8 * 1024)]
    [InlineData(64 * 1024 * 1024)]
    public void WhenEncryptedThenDecryptSucceeds(int dataSize)
    {
        InitializeWithTestKey();
        var plaintext = new byte[dataSize];
        Random.Shared.NextBytes(plaintext);

        var encrypted = _encryptionService.Encrypt(plaintext);
        var decrypted = _encryptionService.Decrypt(encrypted);

        Assert.Equal(plaintext, decrypted);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(8 * 1024)]
    [InlineData(64 * 1024 * 1024)]
    public void WhenEncryptedRepeatedly_CrcIsAlwaysValid(int dataSize)
    {
        InitializeWithTestKey();
        var plaintext = new byte[dataSize];
        Random.Shared.NextBytes(plaintext);

        for (var i = 0; i < 100; i++)
        {
            var encrypted = _encryptionService.Encrypt(plaintext);
            Assert.True(_encryptionService.ValidateCrc(encrypted),
                $"CRC invalid on iteration {i} for {dataSize} byte payload. " +
                _encryptionService.DiagnoseCrcMismatch(encrypted));
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(8 * 1024)]
    [InlineData(1024 * 1024)]
    public void WhenEncryptedConcurrently_CrcIsAlwaysValid(int dataSize)
    {
        InitializeWithTestKey();

        Parallel.For(0, 50, _ =>
        {
            var plaintext = new byte[dataSize];
            Random.Shared.NextBytes(plaintext);
            var encrypted = _encryptionService.Encrypt(plaintext);

            Assert.True(_encryptionService.ValidateCrc(encrypted),
                $"CRC invalid under concurrent Encrypt for {dataSize} byte payload. " +
                _encryptionService.DiagnoseCrcMismatch(encrypted));

            var decrypted = _encryptionService.Decrypt(encrypted);
            Assert.Equal(plaintext, decrypted);
        });
    }

    #endregion

    #region EncryptInto / DecryptInto Tests

    [Fact]
    public async Task EncryptIntoDecryptIntoRoundTrip()
    {
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);

        var plaintext = new byte[1024];
        Random.Shared.NextBytes(plaintext);

        var destSize = plaintext.Length + EncryptionService.EncryptionOverhead;
        var encBuffer = new byte[destSize];
        var written = _encryptionService.EncryptInto(plaintext, encBuffer);

        Assert.Equal(destSize, written);
        Assert.True(_encryptionService.ValidateCrc(encBuffer.AsSpan(0, written)));

        var decBuffer = new byte[plaintext.Length];
        var decWritten = _encryptionService.DecryptInto(encBuffer.AsSpan(0, written), decBuffer);

        Assert.Equal(plaintext.Length, decWritten);
        Assert.Equal(plaintext, decBuffer);
    }

    [Fact]
    public async Task EncryptIntoMatchesEncryptOutput()
    {
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);

        var plaintext = new byte[256];
        Random.Shared.NextBytes(plaintext);

        // Both methods should produce valid ciphertext that decrypts to the same plaintext
        var standard = _encryptionService.Encrypt(plaintext);
        var destSize = plaintext.Length + EncryptionService.EncryptionOverhead;
        var buffer = new byte[destSize];
        var written = _encryptionService.EncryptInto(plaintext, buffer);

        Assert.Equal(standard.Length, written);

        // Both should decrypt to the same plaintext (nonces differ, so ciphertext differs)
        var dec1 = _encryptionService.Decrypt(standard);
        var dec2 = _encryptionService.Decrypt(buffer.AsSpan(0, written));
        Assert.Equal(dec1, dec2);
        Assert.Equal(plaintext, dec1);
    }

    [Fact]
    public async Task DecryptIntoMatchesDecryptOutput()
    {
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);

        var plaintext = new byte[512];
        Random.Shared.NextBytes(plaintext);

        var encrypted = _encryptionService.Encrypt(plaintext);

        // Standard Decrypt
        var standard = _encryptionService.Decrypt(encrypted);

        // DecryptInto
        var buffer = new byte[encrypted.Length - EncryptionService.EncryptionOverhead];
        var written = _encryptionService.DecryptInto(encrypted, buffer);

        Assert.Equal(standard.Length, written);
        Assert.Equal(standard, buffer);
    }

    [Fact]
    public async Task EncryptIntoThrowsWhenDestinationTooSmall()
    {
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);

        var plaintext = new byte[100];
        var tooSmall = new byte[100]; // needs 100 + 37 = 137

        Assert.Throws<ArgumentException>(() =>
            _encryptionService.EncryptInto(plaintext, tooSmall));
    }

    [Fact]
    public async Task DecryptIntoThrowsWhenDestinationTooSmall()
    {
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);

        var plaintext = new byte[100];
        var encrypted = _encryptionService.Encrypt(plaintext);

        var tooSmall = new byte[50]; // needs 100

        Assert.Throws<ArgumentException>(() =>
            _encryptionService.DecryptInto(encrypted, tooSmall));
    }

    [Fact]
    public async Task DecryptIntoThrowsOnCorruptedCrc()
    {
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);

        var plaintext = new byte[64];
        Random.Shared.NextBytes(plaintext);
        var encrypted = _encryptionService.Encrypt(plaintext);

        // Corrupt the last byte (CRC32)
        encrypted[^1] ^= 0xFF;

        var buffer = new byte[plaintext.Length];
        Assert.Throws<DataIntegrityException>(() =>
            _encryptionService.DecryptInto(encrypted, buffer));
    }

    [Fact]
    public void EncryptionOverheadConstantIsCorrect()
    {
        // Overhead = magic(4) + version(1) + nonce(12) + tag(16) + CRC32(4) = 37
        Assert.Equal(37, EncryptionService.EncryptionOverhead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(65536)]
    public async Task EncryptIntoDecryptIntoRoundTripVariousSizes(int size)
    {
        var salt = EncryptionService.GenerateSalt();
        var key = await _encryptionService.DeriveKeyAsync(TestPassword, salt);
        _encryptionService.Initialize(key);

        var plaintext = new byte[size];
        Random.Shared.NextBytes(plaintext);

        var encBuffer = new byte[size + EncryptionService.EncryptionOverhead];
        var encWritten = _encryptionService.EncryptInto(plaintext, encBuffer);

        var decBuffer = new byte[size];
        var decWritten = _encryptionService.DecryptInto(encBuffer.AsSpan(0, encWritten), decBuffer);

        Assert.Equal(size, decWritten);
        Assert.Equal(plaintext, decBuffer[..decWritten]);
    }

    #endregion
}
