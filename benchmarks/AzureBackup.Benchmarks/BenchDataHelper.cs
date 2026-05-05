namespace AzureBackup.Benchmarks;

/// <summary>
/// Shared helpers for the Phase 5 reverse-chunk-index benchmark family.
/// </summary>
internal static class BenchDataHelper
{
    /// <summary>
    /// Deterministic 64-char hex derived from the seed; not cryptographic.
    /// </summary>
    public static string HashString(int seed)
    {
        Span<byte> bytes = stackalloc byte[32];
        BitConverter.TryWriteBytes(bytes, seed);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
