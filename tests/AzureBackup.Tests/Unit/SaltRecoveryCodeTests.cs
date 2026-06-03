using System.Security.Cryptography;
using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for <see cref="SaltRecoveryCode"/> covering round-trip fidelity,
/// human-friendly input tolerance, and checksum/length validation.
/// </summary>
public class SaltRecoveryCodeTests
{
    private static byte[] SampleSalt()
    {
        var salt = new byte[SaltRecoveryCode.SaltSize];
        for (var i = 0; i < salt.Length; i++)
            salt[i] = (byte)(i * 17 + 3);
        return salt;
    }

    [Fact]
    public void EncodeThenDecodeReturnsOriginalSalt()
    {
        // Arrange
        var salt = SampleSalt();

        // Act
        var recovered = SaltRecoveryCode.Decode(SaltRecoveryCode.Encode(salt));

        // Assert
        Assert.Equal(salt, recovered);
    }

    [Fact]
    public void EncodeRandomSaltRoundTrips()
    {
        // Arrange
        var salt = RandomNumberGenerator.GetBytes(SaltRecoveryCode.SaltSize);

        // Act
        var recovered = SaltRecoveryCode.Decode(SaltRecoveryCode.Encode(salt));

        // Assert
        Assert.Equal(salt, recovered);
    }

    [Fact]
    public void EncodeProducesHyphenGroupedCode()
    {
        // Arrange
        var salt = SampleSalt();

        // Act
        var code = SaltRecoveryCode.Encode(salt);

        // Assert
        Assert.Contains('-', code);
    }

    [Fact]
    public void EncodeUsesOnlyCrockfordAlphabet()
    {
        // Arrange
        var salt = SampleSalt();
        const string allowed = "0123456789ABCDEFGHJKMNPQRSTVWXYZ-";

        // Act
        var code = SaltRecoveryCode.Encode(salt);

        // Assert
        Assert.All(code, c => Assert.Contains(c, allowed));
    }

    [Fact]
    public void DecodeIsCaseInsensitive()
    {
        // Arrange
        var salt = SampleSalt();
        var code = SaltRecoveryCode.Encode(salt);

        // Act
        var recovered = SaltRecoveryCode.Decode(code.ToLowerInvariant());

        // Assert
        Assert.Equal(salt, recovered);
    }

    [Fact]
    public void DecodeIgnoresSeparatorsAndWhitespace()
    {
        // Arrange
        var salt = SampleSalt();
        var code = SaltRecoveryCode.Encode(salt).Replace("-", "");

        // Act
        var recovered = SaltRecoveryCode.Decode($"  {code}  ");

        // Assert
        Assert.Equal(salt, recovered);
    }

    [Theory]
    [InlineData('I', '1')]
    [InlineData('L', '1')]
    [InlineData('O', '0')]
    public void DecodeAcceptsCrockfordAliases(char ambiguous, char canonical)
    {
        // Arrange
        var salt = SampleSalt();
        var canonicalCode = SaltRecoveryCode.Encode(salt);
        // Only substitute when the canonical digit actually appears, so the
        // alias maps back to the same symbol.
        var aliased = canonicalCode.Replace(canonical, ambiguous);

        // Act
        var recovered = SaltRecoveryCode.Decode(aliased);

        // Assert
        Assert.Equal(salt, recovered);
    }

    [Fact]
    public void DecodeWithWrongChecksumThrowsFormatException()
    {
        // Arrange
        var code = SaltRecoveryCode.Encode(SampleSalt()).Replace("-", "");
        // Flip the final checksum symbol to a different valid alphabet char.
        var last = code[^1];
        var replacement = last == '0' ? '1' : '0';
        var tampered = code[..^1] + replacement;

        // Act + Assert
        Assert.Throws<FormatException>(() => SaltRecoveryCode.Decode(tampered));
    }

    [Fact]
    public void DecodeTooShortThrowsFormatException()
    {
        // Act + Assert
        Assert.Throws<FormatException>(() => SaltRecoveryCode.Decode("ABCDE-ABCDE"));
    }

    [Fact]
    public void DecodeTooLongThrowsFormatException()
    {
        // Arrange
        var tooLong = SaltRecoveryCode.Encode(SampleSalt()) + "ABCDE";

        // Act + Assert
        Assert.Throws<FormatException>(() => SaltRecoveryCode.Decode(tooLong));
    }

    [Fact]
    public void DecodeInvalidCharacterThrowsFormatException()
    {
        // Arrange: 'U' is excluded from the Crockford alphabet.
        var code = SaltRecoveryCode.Encode(SampleSalt()).Replace("-", "");
        var withInvalid = "U" + code[1..];

        // Act + Assert
        Assert.Throws<FormatException>(() => SaltRecoveryCode.Decode(withInvalid));
    }

    [Fact]
    public void DecodeNullThrowsArgumentNullException()
    {
        // Act + Assert
        Assert.Throws<ArgumentNullException>(() => SaltRecoveryCode.Decode(null!));
    }

    [Fact]
    public void EncodeWrongSizeThrowsArgumentException()
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(() => SaltRecoveryCode.Encode(new byte[8]));
    }

    [Fact]
    public void EncodeNullThrowsArgumentNullException()
    {
        // Act + Assert
        Assert.Throws<ArgumentNullException>(() => SaltRecoveryCode.Encode(null!));
    }

    [Fact]
    public void TryDecodeValidCodeReturnsTrueAndSalt()
    {
        // Arrange
        var salt = SampleSalt();
        var code = SaltRecoveryCode.Encode(salt);

        // Act
        var ok = SaltRecoveryCode.TryDecode(code, out var recovered);

        // Assert
        Assert.True(ok);
        Assert.Equal(salt, recovered);
    }

    [Fact]
    public void TryDecodeInvalidCodeReturnsFalse()
    {
        // Act
        var ok = SaltRecoveryCode.TryDecode("not-a-valid-code", out _);

        // Assert
        Assert.False(ok);
    }
}
