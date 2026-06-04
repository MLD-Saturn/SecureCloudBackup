using System.Runtime.InteropServices;

namespace AzureBackup.Core.Services;

/// <summary>
/// 64-byte-padded <see cref="long"/> for use as a hot atomic counter that lives
/// next to other counters on the stack or heap. The padding pushes adjacent
/// counters onto separate cache lines so concurrent <see cref="Interlocked"/>
/// updates from multiple cores no longer false-share.
/// </summary>
/// <remarks>
/// <para>
/// The CDC consumer fan-out (<c>MaxParallelChunkUploads</c> tasks) repeatedly
/// updates two adjacent counters per chunk: <c>bytesUploaded</c> and
/// <c>chunksUploadedCount</c>. On x64 they end up sharing a 64-byte cache line,
/// so every <see cref="Interlocked.Add(ref long, long)"/> on one invalidates
/// the other line in every other core's cache. With 8 consumers this turned
/// into measurable cache-line ping-pong (Phase 6 / discovered-#2).
/// </para>
/// <para>
/// <c>StructLayout(LayoutKind.Explicit)</c> with the value at offset 0 and
/// <c>Size = 64</c> guarantees the struct occupies a full cache line, even
/// when allocated as a stack local or as a boxed field in a closure capture.
/// Use <see cref="GetRef"/> to obtain a <c>ref long</c> for use with the
/// existing <see cref="Interlocked"/> overloads.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct PaddedLong
{
    [FieldOffset(0)]
    private long _value;

    /// <summary>
    /// Reads the current value with acquire semantics.
    /// </summary>
    public long Read() => Interlocked.Read(ref _value);

    /// <summary>
    /// Atomically adds <paramref name="amount"/> and returns the new value.
    /// </summary>
    public long Add(long amount) => Interlocked.Add(ref _value, amount);

    /// <summary>
    /// Atomically increments and returns the new value.
    /// </summary>
    public long Increment() => Interlocked.Increment(ref _value);

    /// <summary>
    /// Atomically replaces the value and returns the previous one.
    /// </summary>
    public long Exchange(long newValue) => Interlocked.Exchange(ref _value, newValue);
}
