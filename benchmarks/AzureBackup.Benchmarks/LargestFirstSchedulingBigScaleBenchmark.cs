using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace AzureBackup.Benchmarks;

/// <summary>
/// B27: re-run of the LPT scheduling experiment
/// (<see cref="LargestFirstSchedulingBenchmark"/>) against the
/// big-scale workloads with the recommended 16 GB MemoryBudget. The
/// pre-B27 run showed LPT winning -25% on <c>large-skew-200</c> (two
/// 500 MB outliers) but regressing +15-17% on the two most
/// realistic workloads (<c>mixed-realistic-1000</c> and
/// <c>realistic-large-200</c>). The W2 hypothesis for that
/// regression was that LPT destroys the steady-state pipelining that
/// keeps the SqliteBackend write lock contended at a healthy rate;
/// the alternative explanation is that the outliers in the
/// pre-B27 workloads simply were not big enough to dominate the
/// makespan.
///
/// <para>
/// <b>What this benchmark resolves.</b> The <c>huge-outlier-mixed</c>
/// workload has two outliers (10 GB and 20 GB) against 498 small
/// (1-5 MB) files. The outliers dominate the makespan by an order of
/// magnitude. This is the cleanest possible LPT case:
/// <list type="bullet">
///   <item>If LPT wins decisively on <c>huge-outlier-mixed</c> AND
///     still regresses <c>production-scale-3000</c>, the W2
///     steady-state-pipelining hypothesis is confirmed. The right
///     production change is a workload-aware scheduler that applies
///     LPT only when size variance exceeds some threshold.</item>
///   <item>If LPT wins on <c>huge-outlier-mixed</c> AND wins or is
///     flat on <c>production-scale-3000</c>, the pre-B27 regression
///     was an artifact of the under-sized workloads and a blanket
///     largest-first sort is safe to ship.</item>
///   <item>If LPT loses even on <c>huge-outlier-mixed</c>, something
///     much weirder is going on in the pipeline and we need a
///     separate investigation before touching scheduling at all.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Cost.</b> <c>media-library-500</c> is the most expensive
/// (~260 GB disk, two iterations per variant). Consider filtering to
/// <c>huge-outlier-mixed</c> first -- it is the most informative
/// single workload and the cheapest in wall clock.
/// </para>
///
/// <para>
/// <b>Results (pending -- to be captured on this machine):</b>
/// <code>
/// // | Workload                | Input-order Mean | Largest-first Mean | Delta |
/// // |------------------------ |----------------: |------------------: |-----: |
/// // | media-library-500       |                  |                    |       |
/// // | production-scale-3000   |                  |                    |       |
/// // | huge-outlier-mixed      |                  |                    |       |
/// </code>
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class LargestFirstSchedulingBigScaleBenchmark : BackupBenchmarkBase
{
    [ParamsSource(nameof(BigWorkloads))]
    public override string Workload { get; set; } = "huge-outlier-mixed";

    protected override int? MemoryLimitMBOverride => 16384;

    [Benchmark(Baseline = true, Description = "Input-order (today's production)")]
    public async Task InputOrder()
    {
        StartPeakWorkingSetCapture();
        try
        {
            await Orchestrator!.BackupFilesAsync(FilePaths);
        }
        finally
        {
            EmitPeakWorkingSet("InputOrder");
        }
    }

    [Benchmark(Description = "Largest-first (LPT scheduling)")]
    public async Task LargestFirst()
    {
        var sorted = FilePaths
            .Select(p => (path: p, size: new FileInfo(p).Length))
            .OrderByDescending(t => t.size)
            .Select(t => t.path)
            .ToList();
        StartPeakWorkingSetCapture();
        try
        {
            await Orchestrator!.BackupFilesAsync(sorted);
        }
        finally
        {
            EmitPeakWorkingSet("LargestFirst");
        }
    }
}
