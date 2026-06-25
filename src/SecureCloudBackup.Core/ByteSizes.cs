namespace SecureCloudBackup.Core;

/// <summary>
/// Canonical byte-size unit constants. These replace the per-file
/// <c>private const KB/MB</c> declarations that were previously redefined in
/// <c>ChunkingService</c>, <c>ChunkBufferPool</c>, <c>SystemMemoryHelper</c>,
/// and <c>BackupMemoryReporter</c>. Centralizing them keeps every chunk-size,
/// bucket-geometry, and memory-threshold literal reading in the same units.
/// <para>
/// <b>Which constant to use.</b> Reach for <see cref="MB"/> (and <see cref="KB"/>)
/// in <see langword="int"/> contexts: chunk sizes, <c>int[]</c> bucket
/// geometries, and any <c>const int</c> such as
/// <c>ChunkingService.PoolSkipThresholdBytes</c> that must be computed at
/// compile time. Reach for <see cref="MBLong"/> whenever the surrounding
/// expression is 64-bit and a <em>multiply</em> is involved, e.g.
/// <c>selectedMB * ByteSizes.MBLong</c>. Using <see cref="MBLong"/> there makes
/// the safe path the easy path: the result is <see langword="long"/> with no
/// intermediate <see langword="int"/> product that could overflow, and no
/// hand-written <c>(long)</c> cast to forget.
/// </para>
/// <para>
/// <b>Why two names instead of one.</b> A single <see langword="long"/> constant
/// cannot be used in the <c>const int</c> / <c>int[]</c> consumers above (those
/// require an <see langword="int"/>), and a single <see langword="int"/>
/// constant forces every 64-bit multiply site to remember an explicit
/// <c>(long)</c> cast or silently overflow. Two constants of the same magnitude
/// but different width let each call site pick the type that matches its
/// context. The failure mode is safe by design: using <see cref="MBLong"/> in an
/// <see langword="int"/> context is a <em>compile error</em>, not a silent
/// truncation, so the compiler steers a caller to the right one.
/// </para>
/// </summary>
public static class ByteSizes
{
    /// <summary>One kibibyte (1,024 bytes) as an <see langword="int"/>.</summary>
    public const int KB = 1024;

    /// <summary>One mebibyte (1,048,576 bytes) as an <see langword="int"/>.</summary>
    public const int MB = KB * KB;

    /// <summary>
    /// One mebibyte (1,048,576 bytes) as a <see langword="long"/>. Use this in
    /// 64-bit multiply expressions (for example <c>selectedMB * MBLong</c>) so
    /// the product is computed in 64-bit arithmetic and cannot overflow an
    /// intermediate <see langword="int"/>. Numerically identical to
    /// <see cref="MB"/>; the only difference is the type.
    /// </summary>
    public const long MBLong = MB;
}
