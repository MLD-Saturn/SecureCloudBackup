using System.Security.Cryptography;
using System.Text;

namespace AzureBackup.Crypto;

/// <summary>
/// Helper for converting password <see cref="ReadOnlySpan{T}"/> characters into a
/// UTF-8 <c>byte[]</c> that callers can reliably zero when finished, minimising
/// the time plaintext password material lingers on the managed heap.
///
/// <para>
/// This is the single shared implementation used by every key-derivation path in
/// the solution (the Azure content key, the database-snapshot key, the legacy
/// SQLCipher-unlock key in the migration helper). It encodes DIRECTLY from the
/// span into the exact-size destination, so no intermediate <c>char[]</c> or
/// oversized buffer ever holds the plaintext password.
/// </para>
/// </summary>
public static class PasswordBytes
{
    /// <summary>
    /// Encodes <paramref name="password"/> as UTF-8 into a freshly allocated
    /// <c>byte[]</c> whose length matches the encoded byte count exactly. The
    /// returned buffer must be zeroed with
    /// <see cref="CryptographicOperations.ZeroMemory(System.Span{byte})"/> as soon
    /// as it is no longer needed.
    /// </summary>
    /// <remarks>
    /// An exact-size allocation is used instead of <see cref="System.Buffers.ArrayPool{T}"/>
    /// because Argon2id hashes the full <c>Length</c> of the supplied byte array,
    /// so any trailing unused bytes would alter the derived key. Encoding straight
    /// from the span avoids materialising an intermediate <c>char[]</c> copy of the
    /// secret (which could not be zeroed).
    /// </remarks>
    public static byte[] FromChars(ReadOnlySpan<char> password)
    {
        var count = Encoding.UTF8.GetByteCount(password);
        var buffer = new byte[count];
        Encoding.UTF8.GetBytes(password, buffer);
        return buffer;
    }
}
