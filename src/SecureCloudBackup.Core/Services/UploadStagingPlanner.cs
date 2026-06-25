namespace SecureCloudBackup.Core.Services;

/// <summary>
/// Single source of truth for the per-upload staging "shape" -- how many parallel
/// blocks of what size a provider stages while one encrypted chunk is in flight.
/// Lives in Core (not a provider) because the producer-side
/// <see cref="MemoryBudget"/> charge in <see cref="ChunkingService"/> must account
/// for that residency, and Core must stay provider-neutral. A provider maps the
/// same plan onto its SDK's transfer options, so the budget estimate and the
/// actual staging move in lockstep by construction.
/// <para>
/// Bands (encrypted length -&gt; concurrency x block size): &lt;= 8 MB: 1 x len;
/// &lt;= 16 MB: 2 x 8 MB; &lt;= 32 MB: 4 x 8 MB; &gt; 32 MB: 8 x 8 MB. This is the
/// B53 chunk-size-gated shape; see architectural facts #25/#27.
/// </para>
/// </summary>
internal static class UploadStagingPlanner
{
    private const int EightMb = 8 * 1024 * 1024;

    /// <summary>
    /// Computes the (parallel block count, block size in bytes) staging plan for an
    /// encrypted payload of <paramref name="encryptedLength"/> bytes (must be positive).
    /// </summary>
    public static (int Concurrency, int BlockSize) Plan(int encryptedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encryptedLength);

        if (encryptedLength <= EightMb)
            return (1, encryptedLength);
        if (encryptedLength <= 16 * 1024 * 1024)
            return (2, EightMb);
        if (encryptedLength <= 32 * 1024 * 1024)
            return (4, EightMb);
        return (8, EightMb);
    }

    /// <summary>
    /// Upper bound on the staging residency a provider holds while a single upload
    /// of <paramref name="encryptedLength"/> bytes is in flight (Concurrency x
    /// BlockSize). The producer-side <see cref="MemoryBudget"/> charge folds this in
    /// so the budget reflects the real per-chunk residency.
    /// </summary>
    public static long EstimateStagingBytes(int encryptedLength)
    {
        var (concurrency, blockSize) = Plan(encryptedLength);
        return (long)concurrency * blockSize;
    }
}
