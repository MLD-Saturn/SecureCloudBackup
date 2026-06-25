namespace SecureCloudBackup.Core.Services;

/// <summary>
/// Bandwidth-adaptive AIMD (additive-increase, multiplicative-decrease)
/// scheduler that dynamically picks the file-level concurrency for a
/// multi-file restore based on observed throughput.
/// <para>
/// The pre-B62 restore pipeline ran every batch at <c>MaxParallelFileRestores
/// = 16</c> regardless of the upstream link. On a 100 Mbps household line that
/// is 16x more concurrent file pipelines than bandwidth can feed, every one of
/// which holds chunk buffers and Azure SDK staging residency against the same
/// shared <see cref="MemoryBudget"/>. The result is the producer/writer/budget
/// ordering deadlock pinned by the watchdog instrumentation: download tasks
/// park in <c>AcquireAsync</c>, the in-order writer waits for the next chunk,
/// and the budget is held by other files' reorder buffers.
/// </para>
/// <para>
/// The scheduler converges on the smallest concurrency that fully utilizes
/// the available bandwidth. It probes upward when throughput is improving,
/// holds when throughput plateaus, and halves on a regression or when the
/// caller signals a stall or transient error. Bandwidth is measured as bytes
/// completed per wall-clock second using a simple EWMA so single-chunk
/// retries do not dominate the signal.
/// </para>
/// <para>
/// Thread-safe: <see cref="RecordBytesCompleted(long)"/> is called from
/// every per-file producer; <see cref="CurrentConnections"/> is read by the
/// batch dispatcher to size the next dispatch wave.
/// </para>
/// </summary>
public sealed class BandwidthScheduler
{
    /// <summary>
    /// EWMA smoothing factor for the throughput signal. 0.3 weights the
    /// most recent sample heavily enough to react to a real bandwidth shift
    /// within ~3 evaluation ticks while still suppressing single-sample noise
    /// from a transient retry. Picked empirically; not configurable.
    /// </summary>
    private const double EwmaAlpha = 0.3;

    /// <summary>
    /// Minimum sample window before an evaluation tick can fire. Below this
    /// the throughput signal is too noisy to drive a decision -- a partially
    /// completed large chunk would dominate the average. Held just shy of the
    /// chunk-retry base delay so a single transient retry never looks like a
    /// bandwidth regression.
    /// </summary>
    private static readonly TimeSpan MinSampleWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Throughput delta (as a fraction of the previous EWMA) below which an
    /// evaluation tick is considered a "plateau" rather than an "improvement"
    /// or a "regression". 5 percent is wide enough that EWMA jitter on a
    /// stable link does not cause oscillation between additive-increase and
    /// multiplicative-decrease.
    /// </summary>
    private const double PlateauBand = 0.05;

    private readonly int _minConnections;
    private readonly int _maxConnections;
    private readonly Func<long> _nowTicks;
    private readonly Lock _evaluationLock = new();
    // B63: renamed from `_slotReleased` -- the semaphore is released both when a
    // slot is given back AND when an additive-increase step grants new capacity.
    // The old name implied only the first cause.
    private readonly SemaphoreSlim _acquireWakeups = new(0, int.MaxValue);

    private int _currentConnections;
    private int _activeSlots;
    private int _peakActiveSlots;
    private long _bytesSinceLastTick;
    private long _lastTickTicks;
    private double _ewmaBytesPerSecond;
    private long _stallSignals;
    private long _errorSignals;
    private long _additiveIncreases;
    private long _multiplicativeDecreases;
    private long _holds;

    /// <summary>
    /// Creates a bandwidth-adaptive scheduler with the given bounds.
    /// </summary>
    /// <param name="initialConnections">Starting concurrency. Picked low so the
    /// first probe never overshoots a slow link; the additive-increase loop
    /// converges upward within seconds when the link can support more.</param>
    /// <param name="minConnections">Floor. The scheduler never drops below
    /// this even on repeated stalls; a tiny floor (1-2) preserves forward
    /// progress on heavily congested links.</param>
    /// <param name="maxConnections">Ceiling. The scheduler never grows above
    /// this even on a fat link; a high ceiling (32-64) lets a 10 Gbps link
    /// fully saturate without overshooting the per-file budget contention
    /// that pre-B62 saw at 16 files.</param>
    /// <param name="nowTicksProvider">Optional injected wall-clock source
    /// (returns ticks). Tests inject a fake clock to drive evaluation
    /// deterministically; production passes <see langword="null"/> so the
    /// scheduler reads <see cref="Environment.TickCount64"/>.</param>
    public BandwidthScheduler(
        int initialConnections = 4,
        int minConnections = 2,
        int maxConnections = 32,
        Func<long>? nowTicksProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialConnections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minConnections, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, minConnections);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialConnections, minConnections);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialConnections, maxConnections);

        _minConnections = minConnections;
        _maxConnections = maxConnections;
        _nowTicks = nowTicksProvider ?? (() => Environment.TickCount64);
        _currentConnections = initialConnections;
        _lastTickTicks = _nowTicks();
    }

    /// <summary>Lower bound on concurrency; the scheduler never drops below this.</summary>
    public int MinConnections => _minConnections;

    /// <summary>Upper bound on concurrency; the scheduler never grows above this.</summary>
    public int MaxConnections => _maxConnections;

    /// <summary>
    /// The current scheduling decision. Read by the batch dispatcher to
    /// size the next wave of file restores. Reads are atomic; the value
    /// can change between two reads as evaluation ticks fire.
    /// </summary>
    public int CurrentConnections => Volatile.Read(ref _currentConnections);

    /// <summary>
    /// The current EWMA throughput estimate in bytes per second. Surfaced
    /// for diagnostics and metrics; not used internally outside evaluation.
    /// </summary>
    public double EwmaBytesPerSecond
    {
        get { lock (_evaluationLock) { return _ewmaBytesPerSecond; } }
    }

    /// <summary>Number of additive-increase steps since construction.</summary>
    public long AdditiveIncreases => Volatile.Read(ref _additiveIncreases);

    /// <summary>Number of multiplicative-decrease steps since construction.</summary>
    public long MultiplicativeDecreases => Volatile.Read(ref _multiplicativeDecreases);

    /// <summary>Number of plateau holds since construction.</summary>
    public long Holds => Volatile.Read(ref _holds);

    /// <summary>Number of stall notifications received since construction.</summary>
    public long StallSignals => Volatile.Read(ref _stallSignals);

    /// <summary>Number of transient-error notifications received since construction.</summary>
    public long ErrorSignals => Volatile.Read(ref _errorSignals);

    /// <summary>
    /// Number of file-level slots currently in use. Read-only diagnostic;
    /// always satisfies <c>ActiveSlots &lt;= CurrentConnections</c> after the
    /// slot pool has fully drained, though transient excess can occur because
    /// <see cref="CurrentConnections"/> can shrink while existing slots are
    /// still held. Surfaced for diagnostics only.
    /// </summary>
    public int ActiveSlots => Volatile.Read(ref _activeSlots);

    /// <summary>
    /// B63: high-water mark of <see cref="ActiveSlots"/> over the lifetime of
    /// this scheduler. Surfaced so the batch dispatcher can record the actual
    /// peak file-level concurrency in operation metrics rather than the static
    /// <c>MaxParallelFileRestores</c> ceiling, which post-B62 no longer reflects
    /// what the AIMD controller actually used. Updated lock-free via CAS inside
    /// <see cref="AcquireSlotAsync"/>; reads are atomic.
    /// </summary>
    public int PeakActiveSlots => Volatile.Read(ref _peakActiveSlots);

    /// <summary>
    /// Acquires a file-level scheduling slot, waiting if the current
    /// concurrency is fully used. Used by the batch restore dispatcher to
    /// gate per-file admission on the AIMD-controlled <see cref="CurrentConnections"/>
    /// rather than a fixed <c>ParallelOptions.MaxDegreeOfParallelism</c>. The
    /// caller MUST eventually call <see cref="ReleaseSlot"/> exactly once;
    /// the disposable returned by <see cref="AcquireSlotAsync"/> is the
    /// preferred ergonomic wrapper.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token; cancellation
    /// throws <see cref="OperationCanceledException"/> and does NOT increment
    /// the active-slot count.</param>
    public async Task<IAsyncDisposable> AcquireSlotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int active = Volatile.Read(ref _activeSlots);
            int allowed = Volatile.Read(ref _currentConnections);
            if (active < allowed)
            {
                int newActive = active + 1;
                if (Interlocked.CompareExchange(ref _activeSlots, newActive, active) == active)
                {
                    // B63: track high-water mark for operation-level metrics. CAS loop
                    // because multiple acquires can race; whoever observes the largest
                    // value commits it.
                    int currentPeak;
                    while ((currentPeak = Volatile.Read(ref _peakActiveSlots)) < newActive &&
                           Interlocked.CompareExchange(ref _peakActiveSlots, newActive, currentPeak) != currentPeak)
                    {
                    }
                    return new SlotHandle(this);
                }
                continue;
            }
            // Wait until a slot is released or additive-increase grants more
            // capacity, then re-check the (possibly shrunk) allowed value. A
            // multiplicative-decrease step can shrink `_currentConnections`
            // below the active count without itself releasing any slot, so the
            // wait here cannot rely on the allowed value alone -- we re-
            // evaluate on every wakeup.
            await _acquireWakeups.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases a previously acquired file-level slot. Pairs with
    /// <see cref="AcquireSlotAsync"/>; prefer the disposable returned by
    /// <c>await using var slot = await scheduler.AcquireSlotAsync(ct)</c>
    /// rather than calling this directly.
    /// </summary>
    public void ReleaseSlot()
    {
        // B63: suppress spurious wakeups when the post-decrement active count
        // is still at or above the allowed ceiling. Pre-B63 every release
        // woke one waiter, which then re-parked because `active >= allowed`.
        // Under multiplicative-decrease pressure with many parked waiters,
        // that produced quadratic wakeup churn. Eventual additive-increase
        // steps still wake waiters via their own _acquireWakeups.Release(N),
        // so skipping the wakeup here is safe -- we only skip when no waiter
        // could possibly make progress on this slot.
        var newActive = Interlocked.Decrement(ref _activeSlots);
        if (newActive < Volatile.Read(ref _currentConnections))
            _acquireWakeups.Release();
    }

    private sealed class SlotHandle : IAsyncDisposable
    {
        private readonly BandwidthScheduler _owner;
        private int _disposed;
        public SlotHandle(BandwidthScheduler owner) { _owner = owner; }
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.ReleaseSlot();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Records that <paramref name="bytes"/> have completed (downloaded and
    /// written) since the last call. Folded into the EWMA throughput signal
    /// at the next evaluation tick; called from every per-file producer.
    /// </summary>
    public void RecordBytesCompleted(long bytes)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _bytesSinceLastTick, bytes);
        TryEvaluate();
    }

    /// <summary>
    /// Signals that a downstream consumer observed a stall (e.g.
    /// <see cref="MemoryBudget.AcquireAsync"/> blocked for an extended
    /// period). Fires an immediate multiplicative-decrease step regardless
    /// of the EWMA signal -- the caller saw evidence of contention that
    /// the throughput average has not yet reflected.
    /// </summary>
    public void NotifyStallObserved()
    {
        Interlocked.Increment(ref _stallSignals);
        ApplyMultiplicativeDecrease(reason: "stall-observed");
    }

    /// <summary>
    /// Signals that a transient HTTP error (503/429/timeout) was observed.
    /// Fires a multiplicative-decrease step because pushing more concurrency
    /// at a server that is already returning 429s makes the situation worse,
    /// not better. Distinct from stall signals so the two reasons are
    /// independently observable.
    /// </summary>
    public void NotifyTransientError()
    {
        Interlocked.Increment(ref _errorSignals);
        ApplyMultiplicativeDecrease(reason: "transient-error");
    }

    /// <summary>
    /// For tests: advance the evaluation clock and let the AIMD step run
    /// regardless of the natural sample-window cadence. Production callers
    /// must not invoke this directly; use <see cref="RecordBytesCompleted"/>.
    /// </summary>
    internal void ForceEvaluate() => Evaluate(force: true);

    private void TryEvaluate()
    {
        var now = _nowTicks();
        var lastTick = Volatile.Read(ref _lastTickTicks);
        if (now - lastTick < (long)MinSampleWindow.TotalMilliseconds) return;
        Evaluate(force: false);
    }

    private void Evaluate(bool force)
    {
        // B63: compute the wakeup count under the lock, then release the
        // semaphore AFTER exiting the lock. SemaphoreSlim.Release can
        // synchronously run continuations that re-enter scheduler code paths;
        // releasing under the evaluation lock is fragile even if no current
        // continuation actually deadlocks today.
        int wakeupsToRelease = 0;
        lock (_evaluationLock)
        {
            var now = _nowTicks();
            var elapsedMs = now - _lastTickTicks;
            if (!force && elapsedMs < (long)MinSampleWindow.TotalMilliseconds) return;
            if (elapsedMs <= 0) return;

            var bytes = Interlocked.Exchange(ref _bytesSinceLastTick, 0);
            var elapsedSec = elapsedMs / 1000.0;
            var sampleBps = bytes / elapsedSec;

            var prevEwma = _ewmaBytesPerSecond;
            _ewmaBytesPerSecond = prevEwma == 0
                ? sampleBps
                : (EwmaAlpha * sampleBps) + ((1 - EwmaAlpha) * prevEwma);

            _lastTickTicks = now;

            // Need a baseline before any decision can be made. The very first
            // tick just seeds the EWMA; otherwise a one-sample window would
            // immediately trigger an additive increase from no real signal.
            if (prevEwma == 0) return;

            // No bytes at all in the sample window -> nothing was running. The
            // batch is either drained or every worker is parked. Do nothing;
            // the next batch will reset the cadence.
            if (bytes == 0)
            {
                Interlocked.Increment(ref _holds);
                return;
            }

            var delta = (sampleBps - prevEwma) / prevEwma;

            if (delta > PlateauBand)
            {
                wakeupsToRelease = ApplyAdditiveIncrease(delta);
            }
            else if (delta < -PlateauBand)
            {
                ApplyMultiplicativeDecrease(reason: "throughput-regression");
            }
            else
            {
                Interlocked.Increment(ref _holds);
            }
        }

        if (wakeupsToRelease > 0)
            _acquireWakeups.Release(wakeupsToRelease);
    }

    /// <summary>
    /// Variable-step additive increase. AIMD's "additive" is conventionally
    /// +1 per evaluation tick, but on a fast link a +1 climb from
    /// <c>initialConnections=4</c> to a 16-32 ceiling takes 24-56 s of
    /// sustained sample windows. When the EWMA improvement is large
    /// (delta &gt; 4 * <see cref="PlateauBand"/>) we step +2 to halve that
    /// climb without abandoning AIMD's "additive" character. Returns the
    /// number of waiter wakeups the caller must subsequently release
    /// (==step on success, 0 on ceiling).
    /// </summary>
    private int ApplyAdditiveIncrease(double delta)
    {
        var current = Volatile.Read(ref _currentConnections);
        if (current >= _maxConnections)
        {
            Interlocked.Increment(ref _holds);
            return 0;
        }
        var step = delta > PlateauBand * 4 ? 2 : 1;
        var next = Math.Min(_maxConnections, current + step);
        var actualStep = next - current;
        Volatile.Write(ref _currentConnections, next);
        Interlocked.Increment(ref _additiveIncreases);
        // Caller releases `actualStep` semaphore counts AFTER exiting the
        // evaluation lock so a continuation that re-enters scheduler code
        // cannot reacquire it.
        return actualStep;
    }

    private void ApplyMultiplicativeDecrease(string reason)
    {
        _ = reason;
        var current = Volatile.Read(ref _currentConnections);
        var next = Math.Max(_minConnections, current / 2);
        if (next == current)
        {
            Interlocked.Increment(ref _holds);
            return;
        }
        Volatile.Write(ref _currentConnections, next);
        Interlocked.Increment(ref _multiplicativeDecreases);
    }
}
