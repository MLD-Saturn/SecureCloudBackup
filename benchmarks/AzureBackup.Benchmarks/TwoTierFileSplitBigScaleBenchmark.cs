using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace AzureBackup.Benchmarks;

/// <summary>
/// B27: re-run of the file-level concurrency sweep
/// (<see cref="TwoTierFileSplitBenchmark"/>) against the big-scale
/// workloads with the recommended 16 GB MemoryBudget. The pre-B27
/// run recommended bumping <c>MaxParallelFileBackups</c> from 8 to 16
/// based on a -18% win on <c>uniform-1MB-1000</c> and no regressions
/// -- but that recommendation was made against 1,000-file workloads,
/// and the suspected production bottleneck (the SqliteBackend write
/// lock) only really bites at 3,000+ files. At real scale the lock
/// may saturate so completely that higher file concurrency buys
/// nothing or actively hurts.
///
/// <para>
/// <b>What this benchmark resolves.</b> Sweeps file concurrency
/// across {8, 16, 32} on each of the three big-scale workloads:
/// <list type="bullet">
///   <item><c>production-scale-3000</c> is the direct test of the
///     write-lock-saturation hypothesis. If 16-way and 32-way are
///     flat or slower than 8-way here, B27a (bumping the default
///     from 8 to 16) should NOT ship.</item>
///   <item><c>media-library-500</c> tests the opposite regime:
///     fewer files but each producing many chunks. The file-level
///     lock pressure should be low, so any ceiling on throughput
///     here comes from the per-file chunk pipeline, not the lock.</item>
///   <item><c>huge-outlier-mixed</c> is a degenerate case -- two
///     files dominate; file concurrency matters only for the
///     remaining 498 small files.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Peak working set</b> is especially interesting here: at
/// 32-way on <c>media-library-500</c> the theoretical ceiling is
/// 32 workers x 6 chunks x 64 MB x 2 = 24 GB, but the 16 GB
/// MemoryBudget should cap it well below that. If peak WS exceeds
/// ~18 GB on any configuration, the budget is NOT actually binding
/// and we have a separate bug. The benchmark also reports
/// <c>StallCount</c> via the orchestrator's telemetry (visible in
/// the BDN console output from the orchestrator's own Log calls)
/// -- high stall counts indicate the budget is binding tightly and
/// the configuration is probably throughput-limited by memory.
/// </para>
///
/// <para>
/// <b>Results (captured 2026-04-25, hardware: AMD EPYC 7763 @
/// 2.44 GHz, 16 logical / 8 physical cores in Hyper-V, .NET 10.0.6,
/// SQLite backend, MemoryLimitMB=16384, retainPayloads=false,
/// warmupCount=1 iterationCount=2 invocationCount=1):</b>
/// <code>
/// // | Workload                | 8-way    | 16-way   | 32-way   | Best   | 16 vs 8  |
/// // |------------------------ |--------: |--------: |--------: |------- |--------: |
/// // | huge-outlier-mixed      | 2.056 m  | 2.044 m  | 2.024 m  | 32-way |   -0.6%  |
/// // | media-library-500       | 4.721 m  | 3.431 m  | 3.481 m  | 16-way |  -27.3%  |
/// // | production-scale-3000   | 1.726 m  | 1.350 m  | 1.287 m  | 32-way |  -21.8%  |
/// </code>
/// <b>Conclusion</b>: 16-way is the right default. Wins -22 to -27%
/// on the two production-shaped workloads, flat on huge-outlier-mixed
/// (which is dominated by two giant files where file-level
/// parallelism is irrelevant). 32-way is essentially flat against
/// 16-way (within 0.05 m on media-library-500), so 16 is the
/// crossover where the gain has stopped paying. Production default
/// was bumped from 8 to 16 in commit B27 on the strength of this
/// table together with <see cref="TwoTierFileSplitBenchmark"/>'s
/// small-workload data.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class TwoTierFileSplitBigScaleBenchmark : BackupBenchmarkBase
{
    [ParamsSource(nameof(BigWorkloads))]
    public override string Workload { get; set; } = "huge-outlier-mixed";

    [Params(8, 16, 32)]
    public int FileConcurrency { get; set; }

    protected override int? MemoryLimitMBOverride => 16384;

    // B27: discard mode -- see InMemoryBlobService class summary.
    // Without it, media-library-500 cannot fit the encrypted workload
    // in process RAM at any FileConcurrency value.
    protected override bool RetainBlobPayloads => false;

    protected override void ConfigureOrchestrator(BackupOrchestrator orchestrator)
    {
        orchestrator.MaxParallelFileBackupsOverride = FileConcurrency;
    }

    [Benchmark(Description = "Big-scale backup at parametric file concurrency")]
    public async Task Backup()
    {
        StartPeakWorkingSetCapture();
        try
        {
            await Orchestrator!.BackupFilesAsync(FilePaths);
        }
        finally
        {
            EmitPeakWorkingSet($"FileConcurrency={FileConcurrency}");
        }
    }
}
