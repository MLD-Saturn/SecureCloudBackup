using System.Collections.Concurrent;

namespace AzureBackup.Core.Services;

/// <summary>
/// B69 (W5 Phase 3 Commit 1): owned, budget-aware recycler for the
/// SMALL-chunk byte buffer range (64 KB to 16 MB) that
/// <see cref="ChunkingService"/> rents from
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/> on the producer-side
/// chunk-payload allocation path.
///
/// <para>
/// Motivation: the W5 Phase 2 measurement baseline confirmed that the
/// pre-B69 small-chunk path leaks residency through
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/>'s per-core tier
/// caches. ArrayPool keeps a per-core (and per-tier) list of recently
/// returned arrays that lives entirely outside the active
/// <see cref="MemoryBudget"/>; the budget accounting code in
/// <see cref="ChunkingService.AcquireChunkBufferAsync"/> charges the
/// rent's rounded-up tier ceiling (W3 / B30) on acquire and releases the
/// same amount on consumer completion, but the tier caches themselves
/// keep the returned array in process working set indefinitely until the
/// GC reclaims them. On a long-running multi-file backup that retention
/// stacks across every concurrent file worker and forms the bulk of the
/// pre-B69 <c>unaccounted</c> residency the
/// <see cref="MemoryFidelityCollector"/> reported.
/// </para>
///
/// <para>
/// Solution shape: replace the shared per-core tier caches with an
/// operation-scoped pool whose retention IS the budget's residency.
/// Rented buffers are alive forever (the pool retains them in its
/// bucket bags) so the GC never needs to reclaim them between gen-2
/// collections; the per-bucket cap and global byte cap together form
/// the strict residency ceiling. Disposing the pool drains every
/// bucket and lets the GC reclaim the retained arrays once the
/// operation completes. This is the small-chunk twin of B37 / B52's
/// <see cref="LargeChunkBufferPool"/> for the large-chunk path.
/// </para>
///
/// <para>
/// Bucketing: power-of-two-spaced from 64 KB up to 16 MB. The lower
/// bound covers the smallest payload sizes
/// <see cref="ChunkingService"/> rents during CDC tail emission; the
/// upper bound is exactly
/// <see cref="ChunkingService.PoolSkipThresholdBytes"/>, the boundary
/// at which <see cref="ChunkingService.AcquireChunkBufferAsync"/>
/// hands off to <see cref="LargeChunkBufferPool"/> instead. Sizes
/// outside that range allocate fresh and do not flow through the pool
/// -- the producer path never asks for sub-64-KB buffers, so the
/// out-of-range case is defensive only.
/// </para>
///
/// <para>
/// Concurrency: each bucket is a <see cref="ConcurrentBag{T}"/>
/// guarded by an <see cref="Interlocked"/>-managed count, exactly
/// mirroring the <see cref="LargeChunkBufferPool"/> pattern so the two
/// pools have identical contention properties and identical
/// debuggability.
/// </para>
///
/// <para>
/// Interaction with <see cref="MemoryBudget"/>: the pool does NOT
/// itself charge the budget. The producer in
/// <see cref="ChunkingService"/> charges the rent's rounded-up tier
/// ceiling on acquire and releases the same amount on consumer
/// completion exactly as before; the pool replaces the leaky
/// per-core ArrayPool tier caches with operation-scoped bucket bags
/// that share the budget's lifetime. The budget remains the single
/// throttle.
/// </para>
///
/// <para>
/// Lifetime: the pool is a singleton per backup operation, owned by
/// <see cref="BackupOrchestrator"/>. Disposing the pool clears every
/// bucket and lets the GC reclaim the cached buffers; no explicit
/// finalizer is needed because the cached buffers are reachable only
/// through the pool's bucket arrays. Pre-B69 the
/// <see cref="System.Buffers.ArrayPool{T}.Shared"/> tier caches outlived
/// the operation and the next backup inherited stale residency; the
/// owned pool makes that impossible by construction.
/// </para>
/// </summary>
public sealed class BudgetedMemoryPool : IDisposable
{
    private const int KB = 1024;
    private const int MB = 1024 * 1024;

    /// <summary>
    /// Bucket sizes, smallest first. Power-of-two-spaced so the
    /// rounding-up logic in <see cref="GetBucketIndex"/> is a single
    /// linear scan with bounded length. The range starts at 64 KB
    /// (the smallest payload size CDC tail emission can produce) and
    /// ends at exactly 16 MB, matching
    /// <see cref="ChunkingService.PoolSkipThresholdBytes"/> so that
    /// any chunk at or above the threshold flows to
    /// <see cref="LargeChunkBufferPool"/> instead and the two pools
    /// partition the buffer-size axis without overlap.
    /// </summary>
    internal static readonly int[] BucketSizes =
    {
        64 * KB,
        256 * KB,
        1 * MB,
        4 * MB,
        16 * MB
    };

    /// <summary>
    /// Per-bucket capacity in number of cached buffers.
    /// <para>
    /// Sizing rationale: with B27's 16-way file concurrency × 6-way
    /// chunk concurrency = 96 in-flight chunks worst case, and most
    /// small-chunk-path chunks landing in the 1 MB or 4 MB bucket on
    /// the default <see cref="ChunkingService"/> config, a per-bucket
    /// cap of 32 covers roughly one third of the worst-case in-flight
    /// count for any single bucket without unbounded growth. The
    /// bucket caps multiply against <see cref="BucketSizes"/> to give
    /// the worst-case pool residency:
    /// 32 × (64 KB + 256 KB + 1 MB + 4 MB + 16 MB) ~= 32 × 21.3 MB =
    /// ~681 MB. The B52-style global byte cap
    /// (see <see cref="MaxCachedBytes"/>) is the stricter ceiling that
    /// production callers actually use.
    /// </para>
    /// <para>
    /// Tuning knob: if the production memory-log shows the pool's
    /// residency saturating one bucket consistently, raise that
    /// bucket's cap. If the log shows the pool barely populated, the
    /// per-bucket cap is too high and can be lowered without losing
    /// the recycle benefit.
    /// </para>
    /// </summary>
    private const int PerBucketCap = 32;

    private readonly ConcurrentBag<byte[]>[] _buckets;
    private readonly int[] _bucketCounts;
    private readonly long _maxCachedBytes;
    private long _totalBytesCached;
    private long _peakBytesCached;
    private long _totalRents;
    private long _totalRentsFromPool;
    private long _totalReturns;
    private long _totalReturnsAccepted;
    private long _totalReturnsDroppedForCap;
    private int _disposed;

    /// <summary>
    /// Creates a new, empty pool with no global byte cap (only the
    /// per-bucket cap applies). Equivalent to
    /// <c>new BudgetedMemoryPool(long.MaxValue)</c>; preserved for
    /// test and benchmark callers that want the per-bucket-cap-only
    /// ceiling.
    /// </summary>
    public BudgetedMemoryPool() : this(long.MaxValue)
    {
    }

    /// <summary>
    /// Creates a new, empty pool whose total cached residency across
    /// all buckets is bounded by <paramref name="maxCachedBytes"/>.
    /// When a <see cref="Return"/> would push
    /// <see cref="TotalBytesCached"/> above the cap the buffer is
    /// dropped on the floor (the GC reclaims it) instead of being
    /// cached, mirroring the per-bucket overflow behaviour and the
    /// B52 <see cref="LargeChunkBufferPool"/> contract exactly. The
    /// per-bucket cap (<see cref="PerBucketCap"/>) still applies
    /// independently.
    /// <para>
    /// Production callers in <see cref="BackupOrchestrator"/> derive
    /// the cap from the active <see cref="MemoryBudget"/> so the
    /// pool's hidden residency cannot drift past a fraction of the
    /// configured memory limit. Passing <see cref="long.MaxValue"/>
    /// disables the global cap.
    /// </para>
    /// </summary>
    /// <param name="maxCachedBytes">
    /// Maximum total bytes the pool may keep cached across every
    /// bucket. Must be positive. Pass <see cref="long.MaxValue"/>
    /// to disable the global cap.
    /// </param>
    public BudgetedMemoryPool(long maxCachedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCachedBytes);

        _maxCachedBytes = maxCachedBytes;
        _buckets = new ConcurrentBag<byte[]>[BucketSizes.Length];
        _bucketCounts = new int[BucketSizes.Length];
        for (int i = 0; i < BucketSizes.Length; i++)
            _buckets[i] = new ConcurrentBag<byte[]>();
    }

    /// <summary>
    /// Maximum total bytes the pool may keep cached across all
    /// buckets. Returns <see cref="long.MaxValue"/> when no global
    /// cap was configured.
    /// </summary>
    public long MaxCachedBytes => _maxCachedBytes;

    /// <summary>
    /// Number of returned buffers dropped because accepting them
    /// would have pushed <see cref="TotalBytesCached"/> above
    /// <see cref="MaxCachedBytes"/>. A non-zero value confirms the
    /// global cap is binding; when this is consistently zero the
    /// pool's residency is under the cap and the cap is not the
    /// limiting factor.
    /// </summary>
    public long TotalReturnsDroppedForCap => Volatile.Read(ref _totalReturnsDroppedForCap);

    /// <summary>
    /// Total bytes currently cached across all buckets. Snapshot
    /// only; useful for diagnostics and the B36 memory-log emitter.
    /// </summary>
    public long TotalBytesCached => Volatile.Read(ref _totalBytesCached);

    /// <summary>
    /// High-water mark of <see cref="TotalBytesCached"/> over the
    /// lifetime of this pool. Updated on every accepted
    /// <see cref="Return"/> via a lock-free CAS-max loop. Surfaced
    /// through <see cref="BackupMemoryReporter"/> so the operator can
    /// see whether the pool's cap was actually approached during the
    /// operation, distinct from the instantaneous current cached
    /// bytes (which can have been drained by a recent rent).
    /// </summary>
    public long PeakBytesCached => Volatile.Read(ref _peakBytesCached);

    /// <summary>Number of rent calls (pool-served + fresh-allocation).</summary>
    public long TotalRents => Volatile.Read(ref _totalRents);

    /// <summary>Number of rent calls served from the pool's cached buffers.</summary>
    public long TotalRentsFromPool => Volatile.Read(ref _totalRentsFromPool);

    /// <summary>Number of return calls.</summary>
    public long TotalReturns => Volatile.Read(ref _totalReturns);

    /// <summary>Number of return calls that were actually cached (vs dropped on the floor).</summary>
    public long TotalReturnsAccepted => Volatile.Read(ref _totalReturnsAccepted);

    /// <summary>
    /// Pool hit rate as a fraction in [0, 1]. Returns 0 when no
    /// rents have happened yet. A hit rate near 1 means the pool is
    /// doing its job; near 0 means most rents are still allocating
    /// fresh and the pool is providing little residency benefit.
    /// </summary>
    public double HitRate
    {
        get
        {
            var total = TotalRents;
            if (total == 0) return 0.0;
            return (double)TotalRentsFromPool / total;
        }
    }

    /// <summary>
    /// Rents a buffer of at least <paramref name="minimumLength"/>
    /// bytes. Returns a tuple of (buffer, fromPool). When
    /// <c>fromPool</c> is <c>true</c> the returned buffer came from
    /// the pool's cache; when <c>false</c> the request size was
    /// outside the pool's bucket range or the bucket was empty and a
    /// fresh <c>byte[]</c> was allocated (which the caller MUST
    /// still pass back to <see cref="Return"/> so the pool can decide
    /// whether to keep it).
    /// </summary>
    /// <param name="minimumLength">
    /// Minimum required length; the returned buffer's <c>Length</c>
    /// equals the next bucket size at or above this value, or
    /// <paramref name="minimumLength"/> exactly when the request
    /// falls outside the bucket range.
    /// </param>
    public (byte[] Buffer, bool FromPool) Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumLength);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        Interlocked.Increment(ref _totalRents);

        var bucketIndex = GetBucketIndex(minimumLength);
        if (bucketIndex < 0)
        {
            // Outside the pool's bucket range -- allocate fresh.
            return (new byte[minimumLength], false);
        }

        var bucketSize = BucketSizes[bucketIndex];
        if (_buckets[bucketIndex].TryTake(out var cached))
        {
            // Decrement BEFORE marking the rent as pool-served so the
            // count never sits below zero from a transient ordering.
            Interlocked.Decrement(ref _bucketCounts[bucketIndex]);
            Interlocked.Add(ref _totalBytesCached, -bucketSize);
            Interlocked.Increment(ref _totalRentsFromPool);
            return (cached, true);
        }

        // Bucket is empty -- allocate fresh at the bucket's full
        // size (not minimumLength) so the eventual Return call can
        // match a bucket. Returning a smaller-than-bucket array would
        // force a bucket-size mismatch on Return, leaking the buffer
        // out of the pool's recycle path.
        return (new byte[bucketSize], false);
    }

    /// <summary>
    /// Returns a buffer to the pool. The buffer is cached IF its
    /// length matches a bucket size, the bucket is below its
    /// per-bucket cap, AND accepting it would not push the pool's
    /// total residency above <see cref="MaxCachedBytes"/>; otherwise
    /// the buffer is dropped on the floor (the GC will reclaim it).
    /// The combined per-bucket and global caps are the back-pressure
    /// mechanism that keeps the pool's total residency bounded.
    /// </summary>
    /// <param name="buffer">The buffer to return.</param>
    public void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        Interlocked.Increment(ref _totalReturns);

        // Disposed pools accept Return calls silently so callers in a
        // shutdown race do not see exceptions; the GC will reclaim
        // the buffer.
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var bucketIndex = GetBucketIndexForExactSize(buffer.Length);
        if (bucketIndex < 0)
        {
            // Buffer length does not match any bucket -- this can
            // happen if Rent allocated outside the bucket range, or
            // if a caller misuses the API by returning a
            // non-pool-shaped array. Drop the buffer; the GC will
            // reclaim it.
            return;
        }

        var bucketSize = BucketSizes[bucketIndex];

        // Global cap: refuse to cache the buffer when accepting it
        // would push the pool's total residency above
        // _maxCachedBytes. The check is a snapshot read against a
        // possibly-stale total; under contention two concurrent
        // returns can both see a sub-cap value and both proceed to
        // cache. The overshoot is bounded by the size of one bucket
        // entry per concurrent return, which is well below the
        // overall budget headroom this cap is protecting.
        if (_maxCachedBytes != long.MaxValue &&
            Volatile.Read(ref _totalBytesCached) + bucketSize > _maxCachedBytes)
        {
            Interlocked.Increment(ref _totalReturnsDroppedForCap);
            return;
        }

        // Capacity guard: increment only if we are below the
        // per-bucket cap. Compare-and-swap loop avoids serializing
        // the bucket and matches the unsynchronized ConcurrentBag
        // pattern.
        while (true)
        {
            var current = Volatile.Read(ref _bucketCounts[bucketIndex]);
            if (current >= PerBucketCap)
            {
                // At cap; drop on the floor.
                return;
            }
            if (Interlocked.CompareExchange(ref _bucketCounts[bucketIndex], current + 1, current) == current)
                break;
        }

        // Defensive zero-out so a buffer that gets recycled cannot
        // leak previous chunk plaintext into a future chunk's
        // pre-fill window. Cheap relative to the small-chunk-bucket
        // sizes the pool handles.
        Array.Clear(buffer);

        _buckets[bucketIndex].Add(buffer);
        var newTotal = Interlocked.Add(ref _totalBytesCached, BucketSizes[bucketIndex]);

        // CAS-max update of the peak. The loop terminates on the
        // first iteration in the uncontended case and is bounded by
        // the number of concurrent Returns that observed a smaller
        // peak.
        long oldPeak;
        do
        {
            oldPeak = Volatile.Read(ref _peakBytesCached);
            if (newTotal <= oldPeak) break;
        }
        while (Interlocked.CompareExchange(ref _peakBytesCached, newTotal, oldPeak) != oldPeak);

        Interlocked.Increment(ref _totalReturnsAccepted);
    }

    /// <summary>
    /// Bucket index for a rent of <paramref name="minimumLength"/>
    /// bytes, or -1 when the request falls outside the pool's bucket
    /// range. Returns the SMALLEST bucket whose size is greater than
    /// or equal to <paramref name="minimumLength"/>.
    /// </summary>
    private static int GetBucketIndex(int minimumLength)
    {
        for (int i = 0; i < BucketSizes.Length; i++)
        {
            if (minimumLength <= BucketSizes[i])
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Bucket index for a return of an exact-size buffer, or -1 when
    /// the size does not match any bucket. Used to reject Return
    /// calls for buffers that did not originate from this pool's
    /// shape.
    /// </summary>
    private static int GetBucketIndexForExactSize(int length)
    {
        for (int i = 0; i < BucketSizes.Length; i++)
        {
            if (length == BucketSizes[i])
                return i;
        }
        return -1;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Drain every bucket so the cached buffers become unreachable
        // and the GC can reclaim them on the next collection. We do
        // not zero-out here -- Return already zeroed every accepted
        // buffer when it landed in the bucket.
        for (int i = 0; i < _buckets.Length; i++)
        {
            while (_buckets[i].TryTake(out _))
            {
                // discard
            }
            Volatile.Write(ref _bucketCounts[i], 0);
        }
        Volatile.Write(ref _totalBytesCached, 0);
    }
}
