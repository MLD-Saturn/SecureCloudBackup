using System.IO.Hashing;

namespace AzureBackup.Core;

/// <summary>
/// Encodes the 16-byte local-unlock salt sidecar as a short, human-readable
/// recovery code and decodes it back, so a user who accidentally deletes the
/// <c>backup.db.salt</c> file can restore the exact bytes by typing the code.
/// </summary>
/// <remarks>
/// <para>
/// The salt is NOT a secret -- it is shipped inside diagnostic bundles and only
/// makes the Argon2id key derivation unique per install. Without the password
/// the salt grants no decryption power. A recovery code can therefore be written
/// down or stored without weakening the zero-knowledge guarantee; the password
/// remains the only secret. The code does NOT help a user who forgot the
/// password -- nothing can, by design.
/// </para>
/// <para>
/// Encoding is Crockford Base32 (alphabet <c>0-9 A-Z</c> minus the ambiguous
/// <c>I L O U</c>) so the code contains no characters that are easy to mis-read
/// or mis-type. Decoding is case-insensitive and tolerant of the Crockford
/// aliases (<c>I/L -&gt; 1</c>, <c>O -&gt; 0</c>), hyphens, and whitespace. A
/// two-symbol CRC32-derived checksum is appended so a mistyped code is rejected
/// up front instead of silently restoring the wrong salt (which would surface
/// later as an opaque unlock failure).
/// </para>
/// </remarks>
public static class SaltRecoveryCode
{
    /// <summary>The salt the recovery code round-trips, in bytes.</summary>
    public const int SaltSize = KdfParameters.SaltSize;

    // Crockford Base32 symbol set (excludes I, L, O, U).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // 16 bytes = 128 bits; 5 bits per symbol => 26 data symbols (130 bits, last 2 padding).
    private const int DataSymbols = 26;

    // 10-bit checksum => 2 symbols, appended after the data symbols.
    private const int ChecksumSymbols = 2;
    private const int TotalSymbols = DataSymbols + ChecksumSymbols; // 28

    // Symbols per hyphen-separated group in the displayed code.
    private const int GroupSize = 5;

    /// <summary>
    /// Encodes a 16-byte salt as a grouped, human-readable recovery code,
    /// e.g. <c>K7M2Q-9XR4T-PB3WH-...</c>.
    /// </summary>
    /// <param name="salt">The 16-byte salt to encode.</param>
    /// <returns>The recovery code with hyphen-separated groups of five characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="salt"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="salt"/> is not exactly 16 bytes.</exception>
    public static string Encode(byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length != SaltSize)
            throw new ArgumentException($"Salt must be {SaltSize} bytes", nameof(salt));

        Span<char> symbols = stackalloc char[TotalSymbols];
        WriteBase32(salt, symbols[..DataSymbols]);
        WriteChecksumSymbols(salt, symbols.Slice(DataSymbols, ChecksumSymbols));
        return Group(symbols);
    }

    /// <summary>
    /// Decodes a recovery code back to the original 16-byte salt, validating the
    /// checksum. Hyphens, whitespace, and letter case are ignored, and the
    /// Crockford aliases <c>I/L -&gt; 1</c> and <c>O -&gt; 0</c> are accepted.
    /// </summary>
    /// <param name="code">The recovery code to decode.</param>
    /// <returns>The recovered 16-byte salt.</returns>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null or blank.</exception>
    /// <exception cref="FormatException">
    /// The code has the wrong length, contains a character outside the Crockford
    /// alphabet, or fails the checksum (indicating a typo).
    /// </exception>
    public static byte[] Decode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        Span<char> symbols = stackalloc char[TotalSymbols];
        var count = 0;
        foreach (var raw in code)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n')
                continue;
            if (count >= TotalSymbols)
                throw new FormatException(
                    $"Recovery code is too long (expected {TotalSymbols} characters excluding separators).");
            symbols[count++] = Normalize(raw);
        }

        if (count != TotalSymbols)
            throw new FormatException(
                $"Recovery code is too short (expected {TotalSymbols} characters excluding separators, got {count}).");

        var salt = ReadBase32(symbols[..DataSymbols]);

        Span<char> expected = stackalloc char[ChecksumSymbols];
        WriteChecksumSymbols(salt, expected);
        if (!symbols.Slice(DataSymbols, ChecksumSymbols).SequenceEqual(expected))
            throw new FormatException(
                "Recovery code checksum does not match. Re-check the code for a typo.");

        return salt;
    }

    /// <summary>
    /// Returns true and the decoded salt when <paramref name="code"/> is a valid
    /// recovery code; returns false without throwing otherwise. Convenience for
    /// UI input validation.
    /// </summary>
    public static bool TryDecode(string? code, out byte[] salt)
    {
        salt = [];
        if (string.IsNullOrWhiteSpace(code))
            return false;
        try
        {
            salt = Decode(code);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void WriteBase32(ReadOnlySpan<byte> data, Span<char> destination)
    {
        var bitBuffer = 0;
        var bitCount = 0;
        var byteIndex = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            while (bitCount < 5 && byteIndex < data.Length)
            {
                bitBuffer = (bitBuffer << 8) | data[byteIndex++];
                bitCount += 8;
            }

            // Final symbol: left-pad the remaining bits with zeros up to 5.
            var shift = bitCount - 5;
            int index;
            if (shift >= 0)
            {
                index = (bitBuffer >> shift) & 0x1F;
                bitCount -= 5;
            }
            else
            {
                index = (bitBuffer << -shift) & 0x1F;
                bitCount = 0;
            }

            destination[i] = Alphabet[index];
        }
    }

    private static byte[] ReadBase32(ReadOnlySpan<char> symbols)
    {
        var salt = new byte[SaltSize];
        var bitBuffer = 0;
        var bitCount = 0;
        var byteIndex = 0;
        foreach (var symbol in symbols)
        {
            var value = Alphabet.IndexOf(symbol);
            if (value < 0)
                throw new FormatException(
                    $"Recovery code contains an invalid character '{symbol}'.");

            bitBuffer = (bitBuffer << 5) | value;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                if (byteIndex < salt.Length)
                    salt[byteIndex++] = (byte)((bitBuffer >> bitCount) & 0xFF);
            }
        }

        return salt;
    }

    private static void WriteChecksumSymbols(ReadOnlySpan<byte> salt, Span<char> destination)
    {
        var crc = Crc32.HashToUInt32(salt);
        // Low 10 bits of the CRC => two Crockford Base32 symbols.
        destination[0] = Alphabet[(int)((crc >> 5) & 0x1F)];
        destination[1] = Alphabet[(int)(crc & 0x1F)];
    }

    private static string Group(ReadOnlySpan<char> symbols)
    {
        var groups = (symbols.Length + GroupSize - 1) / GroupSize;
        Span<char> buffer = stackalloc char[symbols.Length + groups - 1];
        var pos = 0;
        for (var i = 0; i < symbols.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0)
                buffer[pos++] = '-';
            buffer[pos++] = symbols[i];
        }

        return new string(buffer);
    }

    private static char Normalize(char c)
    {
        var upper = char.ToUpperInvariant(c);
        return upper switch
        {
            'I' or 'L' => '1',
            'O' => '0',
            _ => upper,
        };
    }
}
