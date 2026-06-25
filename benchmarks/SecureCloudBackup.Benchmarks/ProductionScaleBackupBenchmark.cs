using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace SecureCloudBackup.Benchmarks;

/// <summary>
/// B27: end-to-end backup throughput at real production scale with
/// the recommended 16 GB MemoryBudget. First-ever measurement of the
/// pipeline against workloads that actually resemble the user's
/// 2026-04-23 production log (7,313 files with outliers up to 22 GB).
///
/// <para>
/// <b>Why this benchmark exists.</b> The pre-B27
/// <see cref="BackupThroughputBenchmark"/> topped out at 200 files /
/// 100 MB per file because the benchmark author's machine had ~28 GB
/// free. That capped-out matrix told us nothing about three things
/// that actually bite in production:
/// <list type="bullet">
///   <item>How the SqliteBackend write lock behaves at 3,000+ files
///     (the pre-B27 1,000-file matrix was where the
///     <c>mixed-realistic-1000</c> workload started showing the LPT
///     regression; 3,000 is the right scale to see whether the lock
///     has saturated).</item>
///   <item>Whether the chunk concurrency knob matters for files that
///     produce 30+ chunks (pre-B27 <c>realistic-large</c> at 100 MB
///     only produced 2 chunks per file under the media-extension
///     chunk-size path, so adaptive-chunk-concurrency results there
///     were essentially measuring run-to-run noise).</item>
///   <item>Whether the 16 GB MemoryBudget the user should set throttles
///     anything in practice. The pre-B27 runs always used
///     memoryBudget=unlimited so we had zero data on this.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>What it does.</b> Runs the unmodified production pipeline
/// (no concurrency overrides, no scheduling changes) against the
/// three big-scale workloads with <c>MemoryLimitMB=16384</c> applied.
/// The output is the baseline for every other B27 design-decision
/// benchmark -- any <c>*BigScaleBenchmark</c> variant measures a
/// delta against THIS number on the same workload + same budget.
/// </para>
///
/// <para>
/// <b>Cost.</b> Disk: ~260 GB peak (see
/// <c>BackupBenchmarkBase.EstimateWorkloadBytes</c>). Wall clock:
/// expected 30+ minutes per iteration on
/// <c>media-library-500</c>, several minutes on
/// <c>production-scale-3000</c>, and a few minutes on
/// <c>huge-outlier-mixed</c>. Running all three in one invocation
/// with <c>iterationCount=2 warmupCount=1</c> is a multi-hour run.
/// Typical usage is to filter to one workload at a time.
/// </para>
///
/// <para>
/// <b>Results (captured 2026-04-25, hardware: AMD EPYC 7763 @
/// 2.44 GHz, 16 logical / 8 physical cores in Hyper-V, .NET 10.0.6,
/// SQLite backend, MemoryLimitMB=16384, retainPayloads=false,
/// warmupCount=1 iterationCount=2 invocationCount=1; pre-B27
/// orchestrator defaults: MaxParallelChunkUploads=6,
/// MaxParallelFileBackups=8):</b>
/// <code>
/// // | Workload                | Mean    | Allocated  |
/// // |------------------------ |-------: |---------:  |
/// // | huge-outlier-mixed      | 2.036 m |  32.76 GB  |
/// // | media-library-500       | 4.764 m | 256.38 GB  |
/// // | production-scale-3000   | 1.653 m |  75.13 GB  |
/// </code>
/// These are the anchor values that every B27 design-decision
/// benchmark deltas against. The <c>media-library-500</c> Mean here
/// matches <see cref="TwoTierFileSplitBigScaleBenchmark"/> 8-way
/// (4.721 m) within 1%, sanity-checking that both benchmarks measure
/// the same orchestrator under the same configuration.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class ProductionScaleBackupBenchmark : BackupBenchmarkBase
{
    [ParamsSource(nameof(BigWorkloads))]
    public override string Workload { get; set; } = "huge-outlier-mixed";

    /// <summary>
    /// Applies the recommended 16 GB MemoryBudget to every iteration.
    /// Mirrors the production scenario the user intends to run under
    /// (raised from unlimited in response to the multi-GB peak WS
    /// observed in the 2026-04-23 log).
    /// </summary>
    protected override int? MemoryLimitMBOverride => 16384;

    // B27: discard mode. The benchmark host would otherwise need to fit
    // the entire encrypted workload in RAM, which is not possible for
    // media-library-500 (~260 GB) on any commodity machine. Production
    // never retains uploaded ciphertext locally either, so this matches
    // real backup semantics; see the InMemoryBlobService class summary.
    protected override bool RetainBlobPayloads => false;

    [Benchmark(Description = "Production-scale backup, 16 GB memory budget, no overrides")]
    public async Task Backup()
    {
        StartPeakWorkingSetCapture();
        try
        {
            await Orchestrator!.BackupFilesAsync(FilePaths);
        }
        finally
        {
            EmitPeakWorkingSet("ProductionScale");
        }
    }
}
