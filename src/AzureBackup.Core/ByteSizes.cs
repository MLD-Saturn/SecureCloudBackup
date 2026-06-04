namespace AzureBackup.Core;

/// <summary>
/// Canonical byte-size unit constants. These replace the per-file
/// <c>private const KB/MB</c> declarations that were previously redefined in
/// <c>ChunkingService</c>, <c>ChunkBufferPool</c>, <c>SystemMemoryHelper</c>,
/// and <c>BackupMemoryReporter</c>. Centralizing them keeps every chunk-size,
/// bucket-geometry, and memory-threshold literal reading in the same units.
/// <para>
/// Both values are <see langword="int"/> on purpose: the largest production
/// consumer is <c>ChunkBufferPool.LargeChunkBucketSizes</c>, whose
/// <c>256 * MB</c> entry (268,435,456) fits comfortably in an <see langword="int"/>,
/// and <c>ChunkingService.PoolSkipThresholdBytes</c> is a <c>const int</c> that
/// must be computed from <see cref="MB"/> at compile time. Callers that need a
/// 64-bit result (for example a multiply that could exceed <c>int.MaxValue</c>)
/// must widen an operand explicitly, e.g. <c>(long)selectedMB * MB</c>.
/// </para>
/// </summary>
public static class ByteSizes
{
    /// <summary>One kibibyte (1,024 bytes).</summary>
    public const int KB = 1024;

    /// <summary>One mebibyte (1,048,576 bytes).</summary>
    public const int MB = KB * KB;
}
