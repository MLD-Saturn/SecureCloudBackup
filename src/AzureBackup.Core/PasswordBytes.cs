using System.Security.Cryptography;

namespace AzureBackup.Core;

/// <summary>
/// Core-side alias for <see cref="AzureBackup.Crypto.PasswordBytes"/>, which now
/// owns the single password-to-UTF-8 conversion used across the solution. Kept so
/// existing Core call sites compile unchanged; it forwards to the shared
/// implementation rather than duplicating the encoding logic.
/// </summary>
internal static class PasswordBytes
{
    /// <summary>
    /// Encodes <paramref name="password"/> as UTF-8 into a freshly allocated
    /// exact-size <c>byte[]</c>. The returned buffer must be zeroed with
    /// <see cref="CryptographicOperations.ZeroMemory(System.Span{byte})"/> when no
    /// longer needed. See <see cref="AzureBackup.Crypto.PasswordBytes.FromChars"/>.
    /// </summary>
    public static byte[] FromChars(ReadOnlySpan<char> password)
        => AzureBackup.Crypto.PasswordBytes.FromChars(password);
}
