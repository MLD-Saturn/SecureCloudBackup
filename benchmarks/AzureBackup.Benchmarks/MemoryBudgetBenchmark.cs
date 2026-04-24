using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace AzureBackup.Benchmarks;

/// <summary>
/// B27: first-ever measurement of the user-visible MemoryLimitMB
/// slider. Every pre-B27 benchmark ran with
/// <c>memoryBudget=unlimited</c> because the user's 2026-04-23
/// production log showed that setting in use. The recommendation
/// going forward is to run with <c>MemoryLimitMB=16384</c>, so this
/// sweep measures whether that value actually binds and what
/// throughput cost it imposes relative to both unlimited and to
/// lower limits a constrained user might pick.
///
/// <para>
/// <b>Sweep.</b> <c>MemoryLimitParam</c> maps to the settings the UI
/// actually exposes on the slider (512, 1024, 2048, 4096, 8192,
/// 16384, 32768 MB). This benchmark picks a subset:
/// <list type="bullet">
///   <item><c>0</c> (sentinel for unlimited / slider disabled) --
///     the pre-B27 baseline behaviour.</item>
///   <item><c>16384</c> (16 GB) -- the recommended production
///     setting.</item>
///   <item><c>8192</c> (8 GB) -- the "safe for a 16 GB machine"
///     setting most users on commodity hardware would pick.</item>
///   <item><c>4096</c> (4 GB) -- the "tightly constrained" setting
///     for users on older hardware; should expose whether
///     MemoryBudget degrades throughput gracefully or has a hard
///     cliff.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Workload choice.</b> Only <c>media-library-500</c>. It is the
/// workload where MemoryBudget would ever bind: large files produce
/// many simultaneous 64 MB chunk buffers, the theoretical in-flight
/// ceiling is multi-GB, and the production peak WS observation that
/// motivated the recommendation in the first place came from a
/// media-heavy workload. The other two big-scale workloads would
/// either not stress the budget enough
/// (<c>production-scale-3000</c>) or would be dominated by the
/// single-file latency of the 20 GB outlier
/// (<c>huge-outlier-mixed</c>), making the budget-throughput curve
/// unreadable.
/// </para>
///
/// <para>
/// <b>What this benchmark resolves.</b> Produces a defensible
/// default value for the UI's MemoryLimitMB slider and a defensible
/// recommendation for what to do with it. If 4 GB and 16 GB have
/// the same throughput, the slider is cosmetic and we should ship
/// a sensible low default (say 4 GB) with <c>MemoryLimitEnabled</c>
/// ON by default. If 4 GB throttles heavily but 16 GB matches
/// unlimited, 16 GB is the right default for machines with at least
/// 32 GB RAM.
/// </para>
///
/// <para>
/// <b>Results (pending -- to be captured on this machine):</b>
/// <code>
/// // | MemoryLimitParam | Mean (ms) | Peak WS (MB) | StallCount | vs unlimited |
/// // |----------------- |---------: |------------: |----------: |------------: |
/// // | 0 (unlimited)    |           |              |          0 |          +0% |
/// // | 16384            |           |              |            |              |
/// // | 8192             |           |              |            |              |
/// // | 4096             |           |              |            |              |
/// </code>
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class MemoryBudgetBenchmark : BackupBenchmarkBase
{
    [Params("media-library-500")]
    public override string Workload { get; set; } = "media-library-500";

    /// <summary>
    /// MemoryLimitMB value for this iteration. Zero is the sentinel
    /// for the pre-B27 behaviour (unlimited budget / slider disabled).
    /// All other values are applied verbatim as <c>MemoryLimitMB</c>
    /// with <c>MemoryLimitEnabled=true</c>.
    /// </summary>
    [Params(0, 16384, 8192, 4096)]
    public int MemoryLimitParam { get; set; }

    protected override int? MemoryLimitMBOverride =>
        MemoryLimitParam == 0 ? null : MemoryLimitParam;

    [Benchmark(Description = "Backup on media-library-500 at parametric MemoryBudget")]
    public async Task Backup()
    {
        StartPeakWorkingSetCapture();
        try
        {
            await Orchestrator!.BackupFilesAsync(FilePaths);
        }
        finally
        {
            EmitPeakWorkingSet($"MemoryLimitMB={MemoryLimitParam}");
        }
    }
}
