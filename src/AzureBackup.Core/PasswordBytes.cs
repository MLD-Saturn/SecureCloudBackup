using System.Security.Cryptography;
using System.Text;

namespace AzureBackup.Core;

/// <summary>
/// Helper for converting password <see cref="ReadOnlySpan{T}"/> characters into a
/// UTF-8 <c>byte[]</c> that callers can reliably zero when finished, minimising
/// the time plaintext password material lingers on the managed heap.
/// </summary>
internal static class PasswordBytes
{
    /// <summary>
    /// Encodes <paramref name="password"/> as UTF-8 into a freshly allocated
    /// <c>byte[]</c> whose length matches the encoded byte count exactly.
    /// The returned buffer must be zeroed with <see cref="CryptographicOperations.ZeroMemory(System.Span{byte})"/>
    /// as soon as it is no longer needed.
    /// </summary>
    /// <remarks>
    /// An exact-size allocation is used instead of <see cref="System.Buffers.ArrayPool{T}"/>
    /// because Argon2id hashes the full <c>Length</c> of the supplied byte array, so any
    /// trailing unused bytes would alter the derived key.
    /// </remarks>
    public static byte[] FromChars(ReadOnlySpan<char> password)
    {
        var count = Encoding.UTF8.GetByteCount(password);
        var buffer = new byte[count];
        Encoding.UTF8.GetBytes(password, buffer);
        return buffer;
    }
}
