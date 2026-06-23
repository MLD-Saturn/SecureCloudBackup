using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B93: tests for the streaming-download byte-count invariant that
/// distinguishes a client-side parallel-range assembly fault from genuine
/// at-rest corruption. The real <c>DownloadChunkStreamingAsync</c> path runs
/// the 8-way parallel <c>DownloadToAsync</c> against live Azure, which a unit
/// test cannot reach; the decision logic is therefore extracted into the pure
/// static <see cref="AzureBlobService.IsAssembledLengthComplete"/> so the
/// invariant can be pinned deterministically here.
///
/// <para>
/// Why this matters: before B93 the streaming path computed MD5 over
/// <c>buffer[0..MemoryStream.Position]</c> without checking that Position
/// equalled the blob's declared content length. A short read (the SDK
/// under-delivering one of the parallel ranges, or a stale tail left in the
/// POOLED, non-zeroed buffer) made the MD5 fail deterministically and look
/// exactly like "data corrupted at rest" -- which then triggered a recovery
/// pass that zero-filled a perfectly good chunk. B93 turns that into a
/// retryable <see cref="DownloadIntegrityException"/> so a fresh re-download
/// fixes it.
/// </para>
/// </summary>
public sealed class StreamingDownloadIntegrityTests
{
    private const long MB = 1024L * 1024L;

    [Fact]
    public void IsAssembledLengthComplete_ExactMatch_ReturnsTrue()
    {
        Assert.True(AzureBlobService.IsAssembledLengthComplete(17_203_608, 17_203_608));
    }

    [Fact]
    public void IsAssembledLengthComplete_ShortRead_ReturnsFalse()
    {
        // The canonical failure: one parallel 8 MB range silently under-delivered,
        // so the assembly is one block short of the declared content length.
        Assert.False(AzureBlobService.IsAssembledLengthComplete(17_203_608 - 8 * MB, 17_203_608));
    }

    [Fact]
    public void IsAssembledLengthComplete_SingleByteShort_ReturnsFalse()
    {
        // Even a one-byte shortfall must be rejected -- the MD5 would run over
        // a truncated span and fail, and the missing byte could otherwise be
        // backfilled from the pool's previous tenant.
        Assert.False(AzureBlobService.IsAssembledLengthComplete(17_203_607, 17_203_608));
    }

    [Fact]
    public void IsAssembledLengthComplete_OverRead_ReturnsFalse()
    {
        // An over-read (Position past the declared length) is equally wrong:
        // it means the assembly accounting is broken and the trailing bytes
        // are not part of the blob.
        Assert.False(AzureBlobService.IsAssembledLengthComplete(17_203_609, 17_203_608));
    }

    [Fact]
    public void IsAssembledLengthComplete_ZeroLengthBlob_ExactMatchReturnsTrue()
    {
        Assert.True(AzureBlobService.IsAssembledLengthComplete(0, 0));
    }

    [Fact]
    public void IsAssembledLengthComplete_ZeroAssembledNonZeroDeclared_ReturnsFalse()
    {
        // Nothing assembled but the blob is non-empty -- the worst short read.
        Assert.False(AzureBlobService.IsAssembledLengthComplete(0, 17_203_608));
    }

    [Theory]
    [InlineData(64 * 1024L)]
    [InlineData(1 * MB)]
    [InlineData(16 * MB)]
    [InlineData(32 * MB)]
    [InlineData(128 * MB)]
    public void IsAssembledLengthComplete_VariousExactMatches_ReturnTrue(long length)
    {
        Assert.True(AzureBlobService.IsAssembledLengthComplete(length, length));
    }
}
