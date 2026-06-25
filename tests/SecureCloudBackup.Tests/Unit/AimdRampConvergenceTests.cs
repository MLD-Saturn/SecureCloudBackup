using SecureCloudBackup.Core.Services;
using Xunit.Abstractions;

namespace SecureCloudBackup.Tests;

/// <summary>
/// W6 Item 1 (re-run, noise-free): a DETERMINISTIC re-measurement of the AIMD
/// start-low ramp, replacing the N=2-noisy <c>AimdLatencyConvergenceBenchmark</c>
/// whose wall-clock-timing sensitivity made its Means unusable (start-low high-
/// latency arms swung 22 s vs 56 s on two iterations).
///
/// <para>
/// <b>Why this is the optimal instrument.</b> The thing under measurement is a
/// time-gated controller (<see cref="BandwidthScheduler"/> fires one AIMD step
/// per 2 s sample window). BenchmarkDotNet measures it against a real wall
/// clock, so the ramp's outcome depends on exactly how the 2 s windows line up
/// with the run -- intrinsically noisy. Driving the same scheduler through the
/// injected <see cref="FakeClock"/> (the seam <c>BandwidthSchedulerTests</c>
/// already use) removes ALL wall-clock noise: every run is bit-for-bit
/// identical, so the convergence step count and the throughput deficit are
/// exact, not sampled.
/// </para>
///
/// <para>
/// <b>The link model.</b> A real fast link has a bandwidth knee: aggregate
/// throughput rises as more parallel uploads hide round-trip latency, until the
/// pipe saturates, after which extra connections add nothing. Modeled as
/// <c>aggregateBps(n) = min(n * PerConnectionBps, LinkCapacityBps)</c>, so the
/// knee is at <c>kneeConnections = LinkCapacityBps / PerConnectionBps</c>. Each
/// 2 s window the harness reports <c>aggregateBps(CurrentConnections) * 2 s</c>
/// to the scheduler, advances the fake clock 2 s, and forces one evaluation --
/// faithfully reproducing the production cadence. Per-connection throughput is
/// held at a flat <see cref="PerConnectionBps"/>; this is the conservative
/// assumption (real per-connection throughput often falls under contention,
/// which would only make start-low's over-subscription LESS attractive, not
/// more, so it cannot bias the conclusion toward start-ceiling).
/// </para>
///
/// <para>
/// <b>What it computes.</b> For start-low (initial = 4) vs start-at-ceiling
/// (initial = ceiling) it reports: (a) the number of 2 s windows -- hence the
/// wall-clock seconds -- for start-low to reach the knee, and (b) the cumulative
/// transferred-bytes deficit during a fixed backup horizon versus a link pinned
/// at the knee the whole time, expressed as a percentage. (b) is the real
/// production cost: every second spent below the knee transfers less than the
/// link could. The emitted table is the noise-free replacement for the
/// benchmark's unusable Means; the <see cref="ITestOutputHelper"/> log carries
/// the numbers and the asserts lock in the qualitative findings.
/// </para>
/// </summary>
public class AimdRampConvergenceTests
{
    /// <summary>
    /// Test clock that advances explicitly. Mirrors the one in
    /// <c>BandwidthSchedulerTests</c> so the ramp is driven with zero
    /// wall-clock noise.
    /// </summary>
    private sealed class FakeClock
    {
        private long _ticks;
        public long NowTicks() => Volatile.Read(ref _ticks);
        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, (long)delta.TotalMilliseconds);
    }

    /// <summary>
    /// Modeled steady-state throughput of a single upload connection. ~12.5
    /// MB/s == 100 Mbps per connection is a deliberately optimistic per-stream
    /// figure for Azure Blob over a fat pipe; it places the knee for a 1 Gbps
    /// link at ~10 connections and a 2 Gbps link at ~20, straddling the
    /// production large-lane ceiling. The exact value does not change the shape
    /// of the result -- only where the knee falls relative to the ceiling.
    /// </summary>
    private const double PerConnectionBps = 12_500_000d;

    private const int WindowSeconds = 2;
    private const int StartLowInitial = 4;

    /// <summary>
    /// Per-window fraction by which achieved throughput closes the gap to the
    /// target rate set by the current connection count -- the modeled file
    /// pipeline spin-up speed. 0.6 means a fresh slot reaches ~60% of its
    /// contribution in the first window and ~84% in the second, so the rising
    /// signal that drives the AIMD climb is visible but not instant. This sets
    /// only the climb SPEED, never the converged connection count.
    /// </summary>
    private const double WarmupAlpha = 0.6;

    private readonly ITestOutputHelper _output;

    public AimdRampConvergenceTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Result of driving the scheduler through the modeled link for a fixed
    /// number of windows.
    /// </summary>
    private readonly record struct RampResult(
        int WindowsToKnee,
        int FinalConnections,
        long TransferredBytes,
        long SaturatedBytes)
    {
        public double WallClockSecondsToKnee => WindowsToKnee * (double)WindowSeconds;
        public double DeficitFraction => SaturatedBytes == 0
            ? 0d
            : 1d - ((double)TransferredBytes / SaturatedBytes);
    }

    /// <summary>
    /// Drives <paramref name="scheduler"/> through <paramref name="windows"/>
    /// evaluation windows over a link whose knee is at
    /// <paramref name="kneeConnections"/> connections, returning the convergence
    /// and throughput-deficit measurements. Deterministic: same inputs always
    /// produce the same output.
    /// <para>
    /// <b>Why the active-slot warm-up matters.</b> The scheduler increases only
    /// on an OBSERVED throughput improvement (delta &gt; PlateauBand), not by
    /// speculatively adding a connection -- so a perfectly flat per-window
    /// sample produces a plateau and the climb never starts. That is exactly
    /// how production behaves too: the climb is driven by throughput RISING as
    /// the just-granted slots actually start moving bytes. A newly admitted
    /// file pipeline does not hit steady-state throughput within the same 2 s
    /// window it was granted (TLS handshake, first-chunk encrypt/stage), so the
    /// throughput gain from connection N appears over the FOLLOWING window,
    /// which the scheduler then reads as an improvement and grants N+1. We model
    /// that one-window spin-up lag with <c>activeBps</c> chasing the target rate
    /// set by <see cref="BandwidthScheduler.CurrentConnections"/>; the feedback
    /// loop halts on its own at the knee, where extra connections add no
    /// throughput so no further improvement is observed. <see cref="WarmupAlpha"/>
    /// only sets HOW FAST the signal rises; it does not change the converged
    /// connection count.
    /// </para>
    /// </summary>
    private static RampResult DriveLink(
        BandwidthScheduler scheduler,
        FakeClock clock,
        int kneeConnections,
        int windows)
    {
        double linkCapacityBps = kneeConnections * PerConnectionBps;
        long transferred = 0;
        long saturated = 0;
        int windowsToKnee = -1;
        double activeBps = 0;

        for (var i = 0; i < windows; i++)
        {
            int connections = scheduler.CurrentConnections;
            double targetBps = Math.Min(connections * PerConnectionBps, linkCapacityBps);

            // One-window spin-up lag: the achieved throughput chases the target
            // set by the current connection count rather than jumping to it, so
            // a fresh increase shows up as a rising sample next window (the
            // production feedback that sustains the AIMD climb).
            activeBps = activeBps <= 0
                ? targetBps * WarmupAlpha
                : activeBps + ((targetBps - activeBps) * WarmupAlpha);

            long windowBytes = (long)(activeBps * WindowSeconds);

            transferred += windowBytes;
            saturated += (long)(linkCapacityBps * WindowSeconds);

            if (windowsToKnee < 0 && connections >= kneeConnections)
                windowsToKnee = i;

            scheduler.RecordBytesCompleted(windowBytes);
            clock.Advance(TimeSpan.FromSeconds(WindowSeconds));
            scheduler.ForceEvaluate();
        }

        if (windowsToKnee < 0 && scheduler.CurrentConnections >= kneeConnections)
            windowsToKnee = windows;

        return new RampResult(windowsToKnee, scheduler.CurrentConnections, transferred, saturated);
    }

    [Theory]
    [InlineData(10, 16)]  // ~1 Gbps link (knee at 10), production large-lane ceiling 16
    [InlineData(20, 32)]  // ~2 Gbps link (knee at 20), higher ceiling
    public void StartLowReachesTheSameKneeAsStartAtCeiling(int kneeConnections, int ceiling)
    {
        // Run long enough that even a +1-per-window climb reaches the knee.
        const int windows = 40;

        var lowClock = new FakeClock();
        var startLow = new BandwidthScheduler(StartLowInitial, minConnections: 2, maxConnections: ceiling, lowClock.NowTicks);
        var low = DriveLink(startLow, lowClock, kneeConnections, windows);

        var ceilingClock = new FakeClock();
        var startCeiling = new BandwidthScheduler(ceiling, minConnections: 2, maxConnections: ceiling, ceilingClock.NowTicks);
        var high = DriveLink(startCeiling, ceilingClock, kneeConnections, windows);

        // Both must end at or above the knee: start-low's AIMD probing does
        // converge to the saturating concurrency given enough windows, so the
        // ramp is a startup TRANSIENT, not a permanent throughput ceiling.
        Assert.True(low.FinalConnections >= kneeConnections,
            $"start-low converged to {low.FinalConnections}, expected >= knee {kneeConnections}");
        Assert.True(high.FinalConnections >= kneeConnections,
            $"start-ceiling sat at {high.FinalConnections}, expected >= knee {kneeConnections}");
    }

    [Theory]
    [InlineData(10, 16)]
    [InlineData(20, 32)]
    public void StartLowRampCostsMeasurableWallClockTime(int kneeConnections, int ceiling)
    {
        const int windows = 40;

        var clock = new FakeClock();
        var startLow = new BandwidthScheduler(StartLowInitial, minConnections: 2, maxConnections: ceiling, clock.NowTicks);
        var low = DriveLink(startLow, clock, kneeConnections, windows);

        // The ramp is a real, non-trivial wall-clock cost: climbing from 4 to
        // the knee at <= +2 per 2 s window cannot be instant. Lower bound is
        // the best case (+2 every window); we assert it is at least several
        // seconds so a future change that accidentally makes the ramp free
        // (or removes the start-low default) is caught.
        Assert.True(low.WallClockSecondsToKnee >= WindowSeconds * ((kneeConnections - StartLowInitial) / 2),
            $"start-low reached knee {kneeConnections} in {low.WallClockSecondsToKnee}s, faster than the +2/window bound");
    }

    [Fact]
    public void EmitNoiseFreeConvergenceTable()
    {
        // This is the decision instrument: a deterministic table of the
        // start-low ramp cost across representative fast links and backup
        // durations, replacing the noisy benchmark Means.
        (int knee, int ceiling, string link)[] links =
        [
            (10, 16, "~1 Gbps (knee 10, ceiling 16)"),
            (20, 32, "~2 Gbps (knee 20, ceiling 32)"),
        ];

        // Backup horizons to express the ramp penalty as a fraction of total
        // transfer. The ramp is a fixed wall-clock cost, so its fractional
        // penalty shrinks as the backup gets longer.
        int[] horizonSeconds = [30, 60, 120, 300, 600];

        _output.WriteLine("Deterministic AIMD start-low ramp convergence (FakeClock, zero wall-clock noise)");
        _output.WriteLine($"Model: aggregateBps(n) = min(n * {PerConnectionBps / 1_000_000d:0.#} MB/s, linkCapacity); {WindowSeconds}s windows; start-low initial = {StartLowInitial}; warm-up alpha = {WarmupAlpha}");
        _output.WriteLine("Penalty column = bytes that start-low FAILED to transfer vs start-at-ceiling (both pay the same physical pipeline spin-up, so this is the pure AIMD start-low policy cost).");
        _output.WriteLine("");

        foreach (var (knee, ceiling, link) in links)
        {
            // Find the convergence point once with a long run.
            var convClock = new FakeClock();
            var convScheduler = new BandwidthScheduler(StartLowInitial, 2, ceiling, convClock.NowTicks);
            var conv = DriveLink(convScheduler, convClock, knee, windows: 60);

            _output.WriteLine($"Link {link}");
            _output.WriteLine($"  start-low converges to {conv.FinalConnections} connections in {conv.WindowsToKnee} windows = {conv.WallClockSecondsToKnee:0.#}s wall-clock");

            foreach (var horizon in horizonSeconds)
            {
                int windows = horizon / WindowSeconds;

                var lowClock = new FakeClock();
                var low = DriveLink(new BandwidthScheduler(StartLowInitial, 2, ceiling, lowClock.NowTicks), lowClock, knee, windows);

                var ceilClock = new FakeClock();
                var high = DriveLink(new BandwidthScheduler(ceiling, 2, ceiling, ceilClock.NowTicks), ceilClock, knee, windows);

                double aimdPenalty = high.TransferredBytes == 0
                    ? 0d
                    : 1d - ((double)low.TransferredBytes / high.TransferredBytes);

                _output.WriteLine($"  horizon {horizon,4}s: start-low transferred {aimdPenalty * 100,5:0.0}% less than start-at-ceiling (raw deficit vs ideal knee: low {low.DeficitFraction * 100:0.0}%, ceiling {high.DeficitFraction * 100:0.0}%)");
            }

            _output.WriteLine("");
        }

        // The table is the deliverable; this assert just guarantees the
        // instrument ran end to end.
        Assert.True(true);
    }
}
