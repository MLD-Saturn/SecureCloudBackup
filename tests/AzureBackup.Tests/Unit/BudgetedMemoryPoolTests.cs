using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// B69 (W5 Phase 3 Commit 1): tests for <see cref="BudgetedMemoryPool"/>.
/// The pool is the small-chunk recycler that <see cref="ChunkingService"/>
/// rents from for chunks below
/// <see cref="ChunkingService.PoolSkipThresholdBytes"/>; these tests
/// focus on the pool itself in isolation. The integration with the
/// chunking service is exercised separately via the orchestrator tests.
/// <para>
/// These tests mirror <see cref="LargeChunkBufferPoolTests"/> exactly so
/// the two recyclers have identical, auditable contracts. The only
/// per-pool difference is the bucket-size axis: this pool runs
/// 64 KB / 256 KB / 1 MB / 4 MB / 16 MB versus the large pool's
/// 16 MB / 32 MB / 64 MB / 128 MB / 256 MB.
/// </para>
/// </summary>
public class BudgetedMemoryPoolTests
{
    private const int KB = 1024;
    private const int MB = 1024 * 1024;

    [Fact]
    public void RentEmpty_AllocatesFreshAtBucketSize()
    {
        using var pool = new BudgetedMemoryPool();

        var (buffer, fromPool) = pool.Rent(300 * KB);

        Assert.False(fromPool);
        Assert.Equal(1 * MB, buffer.Length); // rounded up to next bucket
    }

    [Fact]
    public void RentReturnRent_ServesFromPool()
    {
        using var pool = new BudgetedMemoryPool();

        var (first, _) = pool.Rent(300 * KB);
        Assert.False(pool.HitRate > 0); // first rent allocates fresh

        pool.Return(first);

        var (second, fromPool) = pool.Rent(300 * KB);
        Assert.True(fromPool);
        Assert.Same(first, second);
    }

    [Fact]
    public void RentExactBucketSize_RoundsToSelf()
    {
        using var pool = new BudgetedMemoryPool();

        var (buffer, _) = pool.Rent(4 * MB);
        Assert.Equal(4 * MB, buffer.Length);
    }

    [Fact]
    public void RentBelowSmallestBucket_AllocatesFreshAtBucketSize()
    {
        // The smallest bucket is 64 KB. A rent of 1 KB rounds up.
        using var pool = new BudgetedMemoryPool();

        var (buffer, _) = pool.Rent(1 * KB);

        Assert.Equal(64 * KB, buffer.Length);
    }

    [Fact]
    public void RentAboveLargestBucket_AllocatesFreshAtRequestedSize()
    {
        // 16 MB is the largest bucket. A rent of 32 MB falls outside
        // the pool entirely and returns an exact-size array.
        using var pool = new BudgetedMemoryPool();

        var (buffer, fromPool) = pool.Rent(32 * MB);

        Assert.False(fromPool);
        Assert.Equal(32 * MB, buffer.Length);
    }

    [Fact]
    public void ReturnArrayOutsideBucketSizes_DropsSilently()
    {
        // A return of an exact-32-MB array does not match any
        // bucket; the pool drops it on the floor without throwing.
        using var pool = new BudgetedMemoryPool();
        var oversized = new byte[32 * MB];

        pool.Return(oversized);

        Assert.Equal(0, pool.TotalBytesCached);
        Assert.Equal(0, pool.TotalReturnsAccepted);
        Assert.Equal(1, pool.TotalReturns);
    }

    [Fact]
    public void ReturnAtCap_DropsSilently()
    {
        // Per-bucket cap is 32. Return 33 buffers of the same
        // bucket size; the 33rd must be dropped.
        using var pool = new BudgetedMemoryPool();
        var buffers = new byte[33][];
        for (int i = 0; i < 33; i++)
            buffers[i] = new byte[64 * KB];

        for (int i = 0; i < 33; i++)
            pool.Return(buffers[i]);

        // 32 accepted, 1 dropped.
        Assert.Equal(33, pool.TotalReturns);
        Assert.Equal(32, pool.TotalReturnsAccepted);
        Assert.Equal(32L * 64 * KB, pool.TotalBytesCached);
    }

    [Fact]
    public void ReturnZeroesBuffer_PreventsCrossChunkLeak()
    {
        // Return must zero the buffer so a recycled rent cannot read
        // stale plaintext from a previous chunk that briefly
        // pre-filled the buffer before being overwritten.
        using var pool = new BudgetedMemoryPool();
        var (buffer, _) = pool.Rent(64 * KB);

        // Stamp a recognizable pattern.
        for (int i = 0; i < 256; i++)
            buffer[i] = 0xCD;

        pool.Return(buffer);

        var (again, fromPool) = pool.Rent(64 * KB);
        Assert.True(fromPool);
        Assert.Same(buffer, again);

        // Every byte must read 0 after Return -- the recycle path is
        // the only way a 0xCD byte could reach this point.
        for (int i = 0; i < 256; i++)
            Assert.Equal((byte)0, again[i]);
    }

    [Fact]
    public void HitRate_ReflectsPoolEffectiveness()
    {
        using var pool = new BudgetedMemoryPool();

        // Cycle 10 buffers through the pool: 10 fresh allocations,
        // 10 returns, then 10 more rents (all served from pool).
        var buffers = new byte[10][];
        for (int i = 0; i < 10; i++)
            buffers[i] = pool.Rent(64 * KB).Buffer;
        for (int i = 0; i < 10; i++)
            pool.Return(buffers[i]);
        for (int i = 0; i < 10; i++)
            pool.Rent(64 * KB);

        // 20 total rents, 10 served from the pool.
        Assert.Equal(20, pool.TotalRents);
        Assert.Equal(10, pool.TotalRentsFromPool);
        Assert.Equal(0.5, pool.HitRate, precision: 2);
    }

    [Fact]
    public void Dispose_ClearsBucketsAndStopsCaching()
    {
        var pool = new BudgetedMemoryPool();
        pool.Return(new byte[64 * KB]);
        Assert.Equal(64L * KB, pool.TotalBytesCached);

        pool.Dispose();

        Assert.Equal(0, pool.TotalBytesCached);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var pool = new BudgetedMemoryPool();
        pool.Dispose();
        pool.Dispose();
        // No exception.
    }

    [Fact]
    public void RentAfterDispose_Throws()
    {
        var pool = new BudgetedMemoryPool();
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Rent(64 * KB));
    }

    [Fact]
    public void ReturnAfterDispose_DropsSilently()
    {
        // Shutdown-race tolerance: a Return call that arrives after
        // Dispose must NOT throw. Producers may call Return on a
        // pool that the orchestrator has already disposed at the
        // end of a backup operation; throwing there would surface
        // an unrelated exception in the cleanup path.
        var pool = new BudgetedMemoryPool();
        pool.Dispose();

        pool.Return(new byte[64 * KB]); // must not throw
    }

    [Fact]
    public void ConcurrentRentReturn_NoCorruptionOrLeak()
    {
        // Stress: 64 threads each cycle 100 rent/return pairs on
        // the smallest bucket. The pool must not throw, must not
        // exceed its cap by more than the natural CAS race window,
        // and must end with TotalBytesCached <= cap * bucketSize.
        using var pool = new BudgetedMemoryPool();
        var threads = new Thread[64];
        var iterationsPerThread = 100;

        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    var (buf, _) = pool.Rent(64 * KB);
                    Assert.NotNull(buf);
                    pool.Return(buf);
                }
            });
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Equal(64 * iterationsPerThread, pool.TotalRents);
        Assert.Equal(64 * iterationsPerThread, pool.TotalReturns);
        // After all threads finish, no rents are outstanding -- so
        // every acceptable return is in the pool. The cap is 32, so
        // residency must be <= 32 * 64 KB.
        Assert.True(pool.TotalBytesCached <= 32L * 64 * KB,
            $"Residency {pool.TotalBytesCached} exceeded cap {32L * 64 * KB}");
    }

    [Fact]
    public void RentZero_Throws()
    {
        using var pool = new BudgetedMemoryPool();

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(0));
    }

    [Fact]
    public void RentNegative_Throws()
    {
        using var pool = new BudgetedMemoryPool();

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1));
    }

    [Fact]
    public void ReturnNull_Throws()
    {
        using var pool = new BudgetedMemoryPool();

        Assert.Throws<ArgumentNullException>(() => pool.Return(null!));
    }

    // ---- global byte-cap behaviour ----

    [Fact]
    public void DefaultConstructor_HasUnlimitedGlobalCap()
    {
        using var pool = new BudgetedMemoryPool();

        Assert.Equal(long.MaxValue, pool.MaxCachedBytes);
    }

    [Fact]
    public void GlobalCap_DropsReturnsThatWouldExceedCap()
    {
        // Cap at 2 MB: the 1 MB bucket fits twice, the third must be
        // dropped.
        using var pool = new BudgetedMemoryPool(maxCachedBytes: 2L * MB);

        pool.Return(new byte[1 * MB]); // accepted: cached=1 MB
        pool.Return(new byte[1 * MB]); // accepted: cached=2 MB
        pool.Return(new byte[1 * MB]); // dropped: would push to 3 MB > 2 MB cap

        Assert.Equal(3, pool.TotalReturns);
        Assert.Equal(2, pool.TotalReturnsAccepted);
        Assert.Equal(1, pool.TotalReturnsDroppedForCap);
        Assert.Equal(2L * MB, pool.TotalBytesCached);
    }

    [Fact]
    public void GlobalCap_AcceptsAgainAfterRentDrainsResidency()
    {
        // Same cap: 2 MB. Fill, then rent one back to drop residency
        // to 1 MB, then a fresh return must be accepted again.
        using var pool = new BudgetedMemoryPool(maxCachedBytes: 2L * MB);
        pool.Return(new byte[1 * MB]);
        pool.Return(new byte[1 * MB]);

        var (rented, fromPool) = pool.Rent(1 * MB);
        Assert.True(fromPool);
        Assert.Equal(1L * MB, pool.TotalBytesCached);

        pool.Return(new byte[1 * MB]); // back to 2 MB cap
        Assert.Equal(2L * MB, pool.TotalBytesCached);
        // Three accepted across the run: two on the initial fill, one
        // after the residency drained back below the cap.
        Assert.Equal(3, pool.TotalReturnsAccepted);
        Assert.Equal(0, pool.TotalReturnsDroppedForCap);
    }

    [Fact]
    public void GlobalCap_PerBucketCapStillApplies()
    {
        // Generous global cap (256 MB), tiny per-bucket cap defaults
        // to 32. A 33rd 64 KB return must still be dropped per the
        // per-bucket rule, even though the global cap has plenty of
        // headroom. Drops in this case are NOT counted under
        // TotalReturnsDroppedForCap because they are per-bucket
        // rejections, not global-cap rejections.
        using var pool = new BudgetedMemoryPool(maxCachedBytes: 256L * MB);
        for (int i = 0; i < 33; i++)
            pool.Return(new byte[64 * KB]);

        Assert.Equal(33, pool.TotalReturns);
        Assert.Equal(32, pool.TotalReturnsAccepted);
        Assert.Equal(0, pool.TotalReturnsDroppedForCap);
        Assert.Equal(32L * 64 * KB, pool.TotalBytesCached);
    }

    [Fact]
    public void GlobalCapZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BudgetedMemoryPool(maxCachedBytes: 0));
    }

    [Fact]
    public void GlobalCapNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BudgetedMemoryPool(maxCachedBytes: -1));
    }

    [Fact]
    public void Dispose_ResetsResidencyButRetainsCap()
    {
        var pool = new BudgetedMemoryPool(maxCachedBytes: 4L * MB);
        pool.Return(new byte[1 * MB]);

        pool.Dispose();

        Assert.Equal(0, pool.TotalBytesCached);
        Assert.Equal(4L * MB, pool.MaxCachedBytes);
    }

    // ---- peak cached residency ----

    [Fact]
    public void NewPool_PeakIsZero()
    {
        using var pool = new BudgetedMemoryPool();

        Assert.Equal(0, pool.PeakBytesCached);
    }

    [Fact]
    public void AcceptedReturn_AdvancesPeak()
    {
        using var pool = new BudgetedMemoryPool();

        pool.Return(new byte[1 * MB]);
        Assert.Equal(1L * MB, pool.PeakBytesCached);

        pool.Return(new byte[4 * MB]);
        Assert.Equal((1L + 4) * MB, pool.PeakBytesCached);
    }

    [Fact]
    public void RentBelowPeak_PeakStays()
    {
        // Peak must be a high-water mark; renting cached buffers
        // back out of the pool drops TotalBytesCached but the peak
        // must remain so a post-hoc reading of the [mem] log shows
        // the worst-case residency the pool ever held.
        using var pool = new BudgetedMemoryPool();
        pool.Return(new byte[1 * MB]);
        pool.Return(new byte[1 * MB]);
        Assert.Equal(2L * MB, pool.PeakBytesCached);

        pool.Rent(1 * MB);
        Assert.Equal(1L * MB, pool.TotalBytesCached);
        Assert.Equal(2L * MB, pool.PeakBytesCached);
    }

    [Fact]
    public void DroppedForCap_DoesNotAdvancePeak()
    {
        // A return rejected by the global cap is NOT cached, so it
        // must not contribute to the high-water mark.
        using var pool = new BudgetedMemoryPool(maxCachedBytes: 1L * MB);
        pool.Return(new byte[1 * MB]);
        Assert.Equal(1L * MB, pool.PeakBytesCached);

        pool.Return(new byte[1 * MB]); // dropped: would push to 2 MB
        Assert.Equal(1L * MB, pool.PeakBytesCached);
        Assert.Equal(1, pool.TotalReturnsDroppedForCap);
    }
}
