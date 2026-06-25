using SecureCloudBackup.Core.Services;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// B70 (W5 Phase 3 Commit 2): tests for the unified
/// <see cref="ChunkBufferPool"/> that replaced both
/// <c>LargeChunkBufferPool</c> (B37) and <c>BudgetedMemoryPool</c>
/// (B69). The same contract must hold for both production bucket
/// geometries, so every behavioural test runs once for
/// <see cref="ChunkBufferPool.SmallChunkBucketSizes"/> (64 KB / 256 KB /
/// 1 MB / 4 MB / 16 MB) and once for
/// <see cref="ChunkBufferPool.LargeChunkBucketSizes"/> (16 MB / 32 MB /
/// 64 MB / 128 MB / 256 MB) via the <see cref="Geometries"/>
/// theory data. The size constants in each test are parameterized so
/// the same assertions cover both ranges without duplicating the
/// test bodies.
/// </summary>
public class ChunkBufferPoolTests
{
    private const int KB = 1024;
    private const int MB = 1024 * 1024;

    /// <summary>
    /// Per-geometry size constants used by every parameterized test.
    /// Pre-rounding is intentional: <see cref="BelowSmallest"/> sits
    /// strictly below the smallest bucket so the rent rounds up to
    /// <see cref="Smallest"/>; <see cref="MidUnaligned"/> sits between
    /// two buckets so the rent rounds up to <see cref="Mid"/>;
    /// <see cref="AboveLargest"/> sits strictly above the largest
    /// bucket so the rent falls outside the pool.
    /// </summary>
    public sealed record Geometry(
        int[] Buckets,
        int BelowSmallest,
        int Smallest,
        int MidUnaligned,
        int Mid,
        int AboveLargest,
        long GlobalCapTwoSmallest);

    public static TheoryData<Geometry> Geometries => new()
    {
        new Geometry(
            Buckets: ChunkBufferPool.SmallChunkBucketSizes,
            BelowSmallest: 1 * KB,
            Smallest: 64 * KB,
            MidUnaligned: 300 * KB,
            Mid: 1 * MB,
            AboveLargest: 32 * MB,
            GlobalCapTwoSmallest: 2L * 64 * KB),
        new Geometry(
            Buckets: ChunkBufferPool.LargeChunkBucketSizes,
            BelowSmallest: 1 * MB,
            Smallest: 16 * MB,
            MidUnaligned: 20 * MB,
            Mid: 32 * MB,
            AboveLargest: 300 * MB,
            GlobalCapTwoSmallest: 2L * 16 * MB)
    };

    [Theory, MemberData(nameof(Geometries))]
    public void RentEmpty_AllocatesFreshAtBucketSize(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        var (buffer, fromPool) = pool.Rent(g.MidUnaligned);

        Assert.False(fromPool);
        Assert.Equal(g.Mid, buffer.Length);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void RentReturnRent_ServesFromPool(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        var (first, _) = pool.Rent(g.MidUnaligned);
        Assert.False(pool.HitRate > 0);

        pool.Return(first);

        var (second, fromPool) = pool.Rent(g.MidUnaligned);
        Assert.True(fromPool);
        Assert.Same(first, second);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void RentExactBucketSize_RoundsToSelf(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        var (buffer, _) = pool.Rent(g.Mid);

        Assert.Equal(g.Mid, buffer.Length);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void RentBelowSmallestBucket_AllocatesFreshAtBucketSize(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        var (buffer, _) = pool.Rent(g.BelowSmallest);

        Assert.Equal(g.Smallest, buffer.Length);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void RentAboveLargestBucket_AllocatesFreshAtRequestedSize(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        var (buffer, fromPool) = pool.Rent(g.AboveLargest);

        Assert.False(fromPool);
        Assert.Equal(g.AboveLargest, buffer.Length);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void ReturnArrayOutsideBucketSizes_DropsSilently(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);
        var oversized = new byte[g.AboveLargest];

        pool.Return(oversized);

        Assert.Equal(0, pool.TotalBytesCached);
        Assert.Equal(0, pool.TotalReturnsAccepted);
        Assert.Equal(1, pool.TotalReturns);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void ReturnAtCap_DropsSilently(Geometry g)
    {
        // Per-bucket cap is 32. Return 33 buffers of the same
        // smallest bucket size; the 33rd must be dropped.
        using var pool = new ChunkBufferPool(g.Buckets);
        for (int i = 0; i < 33; i++)
            pool.Return(new byte[g.Smallest]);

        Assert.Equal(33, pool.TotalReturns);
        Assert.Equal(32, pool.TotalReturnsAccepted);
        Assert.Equal(32L * g.Smallest, pool.TotalBytesCached);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void ReturnZeroesBuffer_PreventsCrossChunkLeak(Geometry g)
    {
        // Return must zero the buffer so a recycled rent cannot read
        // stale plaintext from a previous chunk that briefly
        // pre-filled the buffer before being overwritten.
        using var pool = new ChunkBufferPool(g.Buckets);
        var (buffer, _) = pool.Rent(g.Smallest);

        for (int i = 0; i < 256; i++)
            buffer[i] = 0xCD;

        pool.Return(buffer);

        var (again, fromPool) = pool.Rent(g.Smallest);
        Assert.True(fromPool);
        Assert.Same(buffer, again);

        for (int i = 0; i < 256; i++)
            Assert.Equal((byte)0, again[i]);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void HitRate_ReflectsPoolEffectiveness(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        // 10 fresh allocations, 10 returns, then 10 more rents (all
        // served from the pool).
        var buffers = new byte[10][];
        for (int i = 0; i < 10; i++)
            buffers[i] = pool.Rent(g.Smallest).Buffer;
        for (int i = 0; i < 10; i++)
            pool.Return(buffers[i]);
        for (int i = 0; i < 10; i++)
            pool.Rent(g.Smallest);

        Assert.Equal(20, pool.TotalRents);
        Assert.Equal(10, pool.TotalRentsFromPool);
        Assert.Equal(0.5, pool.HitRate, precision: 2);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void Dispose_ClearsBucketsAndStopsCaching(Geometry g)
    {
        var pool = new ChunkBufferPool(g.Buckets);
        pool.Return(new byte[g.Smallest]);
        Assert.Equal((long)g.Smallest, pool.TotalBytesCached);

        pool.Dispose();

        Assert.Equal(0, pool.TotalBytesCached);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void Dispose_IsIdempotent(Geometry g)
    {
        var pool = new ChunkBufferPool(g.Buckets);
        pool.Dispose();
        pool.Dispose();
        // No exception.
    }

    [Theory, MemberData(nameof(Geometries))]
    public void RentAfterDispose_Throws(Geometry g)
    {
        var pool = new ChunkBufferPool(g.Buckets);
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Rent(g.Smallest));
    }

    [Theory, MemberData(nameof(Geometries))]
    public void ReturnAfterDispose_DropsSilently(Geometry g)
    {
        // Shutdown-race tolerance: a Return call that arrives after
        // Dispose must NOT throw. Producers may call Return on a
        // pool that the orchestrator has already disposed at the
        // end of a backup operation; throwing there would surface
        // an unrelated exception in the cleanup path.
        var pool = new ChunkBufferPool(g.Buckets);
        pool.Dispose();

        pool.Return(new byte[g.Smallest]); // must not throw
    }

    [Theory, MemberData(nameof(Geometries))]
    public void ConcurrentRentReturn_NoCorruptionOrLeak(Geometry g)
    {
        // Stress: 64 threads each cycle 100 rent/return pairs on the
        // smallest bucket. The pool must not throw, must not exceed
        // its per-bucket cap by more than the natural CAS race
        // window, and must end with TotalBytesCached <= cap * smallest.
        using var pool = new ChunkBufferPool(g.Buckets);
        var threads = new Thread[64];
        var iterationsPerThread = 100;

        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    var (buf, _) = pool.Rent(g.Smallest);
                    Assert.NotNull(buf);
                    pool.Return(buf);
                }
            });
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Equal(64 * iterationsPerThread, pool.TotalRents);
        Assert.Equal(64 * iterationsPerThread, pool.TotalReturns);
        Assert.True(pool.TotalBytesCached <= 32L * g.Smallest,
            $"Residency {pool.TotalBytesCached} exceeded cap {32L * g.Smallest}");
    }

    [Theory, MemberData(nameof(Geometries))]
    public void RentZero_Throws(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(0));
    }

    [Theory, MemberData(nameof(Geometries))]
    public void RentNegative_Throws(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1));
    }

    [Theory, MemberData(nameof(Geometries))]
    public void ReturnNull_Throws(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
    }

    // ---- Global byte-cap behaviour (formerly B52) ----

    [Theory, MemberData(nameof(Geometries))]
    public void DefaultConstructor_HasUnlimitedGlobalCap(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        Assert.Equal(long.MaxValue, pool.MaxCachedBytes);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void GlobalCap_DropsReturnsThatWouldExceedCap(Geometry g)
    {
        // Cap at two smallest buckets: two returns at smallest size
        // fit, the third must be dropped for cap.
        using var pool = new ChunkBufferPool(g.Buckets, g.GlobalCapTwoSmallest);

        pool.Return(new byte[g.Smallest]);
        pool.Return(new byte[g.Smallest]);
        pool.Return(new byte[g.Smallest]);

        Assert.Equal(3, pool.TotalReturns);
        Assert.Equal(2, pool.TotalReturnsAccepted);
        Assert.Equal(1, pool.TotalReturnsDroppedForCap);
        Assert.Equal(g.GlobalCapTwoSmallest, pool.TotalBytesCached);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void GlobalCap_AcceptsAgainAfterRentDrainsResidency(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets, g.GlobalCapTwoSmallest);
        pool.Return(new byte[g.Smallest]);
        pool.Return(new byte[g.Smallest]);

        var (_, fromPool) = pool.Rent(g.Smallest);
        Assert.True(fromPool);
        Assert.Equal((long)g.Smallest, pool.TotalBytesCached);

        pool.Return(new byte[g.Smallest]);
        Assert.Equal(g.GlobalCapTwoSmallest, pool.TotalBytesCached);
        Assert.Equal(3, pool.TotalReturnsAccepted);
        Assert.Equal(0, pool.TotalReturnsDroppedForCap);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void GlobalCap_PerBucketCapStillApplies(Geometry g)
    {
        // Generous global cap (well above 32 * smallest); per-bucket
        // cap of 32 still rejects the 33rd return. Per-bucket
        // rejections are NOT counted under TotalReturnsDroppedForCap.
        var generousCap = 1024L * (long)g.Smallest;
        using var pool = new ChunkBufferPool(g.Buckets, generousCap);
        for (int i = 0; i < 33; i++)
            pool.Return(new byte[g.Smallest]);

        Assert.Equal(33, pool.TotalReturns);
        Assert.Equal(32, pool.TotalReturnsAccepted);
        Assert.Equal(0, pool.TotalReturnsDroppedForCap);
        Assert.Equal(32L * g.Smallest, pool.TotalBytesCached);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void GlobalCapZero_Throws(Geometry g)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ChunkBufferPool(g.Buckets, maxCachedBytes: 0));
    }

    [Theory, MemberData(nameof(Geometries))]
    public void GlobalCapNegative_Throws(Geometry g)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ChunkBufferPool(g.Buckets, maxCachedBytes: -1));
    }

    [Theory, MemberData(nameof(Geometries))]
    public void Dispose_ResetsResidencyButRetainsCap(Geometry g)
    {
        var cap = 4L * g.Smallest;
        var pool = new ChunkBufferPool(g.Buckets, cap);
        pool.Return(new byte[g.Smallest]);

        pool.Dispose();

        Assert.Equal(0, pool.TotalBytesCached);
        Assert.Equal(cap, pool.MaxCachedBytes);
    }

    // ---- Peak cached residency (formerly B56) ----

    [Theory, MemberData(nameof(Geometries))]
    public void NewPool_PeakIsZero(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        Assert.Equal(0, pool.PeakBytesCached);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void AcceptedReturn_AdvancesPeak(Geometry g)
    {
        using var pool = new ChunkBufferPool(g.Buckets);

        // Use the smallest two adjacent buckets so the asserts work
        // regardless of geometry.
        var first = g.Buckets[0];
        var second = g.Buckets[1];

        pool.Return(new byte[first]);
        Assert.Equal((long)first, pool.PeakBytesCached);

        pool.Return(new byte[second]);
        Assert.Equal((long)first + second, pool.PeakBytesCached);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void RentBelowPeak_PeakStays(Geometry g)
    {
        // Peak is a high-water mark; renting cached buffers back out
        // of the pool drops TotalBytesCached but the peak must remain
        // so a post-hoc reading of the [mem] log shows the worst-case
        // residency the pool ever held.
        using var pool = new ChunkBufferPool(g.Buckets);
        pool.Return(new byte[g.Smallest]);
        pool.Return(new byte[g.Smallest]);
        Assert.Equal(2L * g.Smallest, pool.PeakBytesCached);

        pool.Rent(g.Smallest);
        Assert.Equal((long)g.Smallest, pool.TotalBytesCached);
        Assert.Equal(2L * g.Smallest, pool.PeakBytesCached);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void DroppedForCap_DoesNotAdvancePeak(Geometry g)
    {
        // A return rejected by the global cap is NOT cached, so it
        // must not contribute to the high-water mark.
        using var pool = new ChunkBufferPool(g.Buckets, g.Smallest);
        pool.Return(new byte[g.Smallest]);
        Assert.Equal((long)g.Smallest, pool.PeakBytesCached);

        pool.Return(new byte[g.Smallest]); // dropped
        Assert.Equal((long)g.Smallest, pool.PeakBytesCached);
        Assert.Equal(1, pool.TotalReturnsDroppedForCap);
    }

    [Theory, MemberData(nameof(Geometries))]
    public void Dispose_DoesNotResetPeak(Geometry g)
    {
        // Dispose drains the live buckets but the peak record of
        // what the pool held during its lifetime must survive so a
        // dispose-time [mem] sample can still report it.
        var first = g.Buckets[0];
        var second = g.Buckets[1];

        var pool = new ChunkBufferPool(g.Buckets);
        pool.Return(new byte[first]);
        pool.Return(new byte[second]);
        Assert.Equal((long)first + second, pool.PeakBytesCached);

        pool.Dispose();

        Assert.Equal(0, pool.TotalBytesCached);
        Assert.Equal((long)first + second, pool.PeakBytesCached);
    }

    // ---- B70-specific: constructor validation of bucket geometry ----

    [Fact]
    public void NullBucketArray_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ChunkBufferPool(null!));
    }

    [Fact]
    public void EmptyBucketArray_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ChunkBufferPool(Array.Empty<int>()));
    }

    [Fact]
    public void NonPositiveBucket_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ChunkBufferPool([0, 1024]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ChunkBufferPool([-1, 1024]));
    }

    [Fact]
    public void NonStrictlyIncreasingBuckets_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new ChunkBufferPool([1024, 1024]));
        Assert.Throws<ArgumentException>(
            () => new ChunkBufferPool([2048, 1024]));
    }

    [Fact]
    public void BucketSizesProperty_ReturnsDefensiveCopy()
    {
        using var pool = new ChunkBufferPool(ChunkBufferPool.SmallChunkBucketSizes);

        var snap1 = pool.BucketSizes;
        snap1[0] = -999;

        var snap2 = pool.BucketSizes;
        Assert.NotEqual(snap1[0], snap2[0]);
        Assert.Equal(ChunkBufferPool.SmallChunkBucketSizes[0], snap2[0]);
    }
}
