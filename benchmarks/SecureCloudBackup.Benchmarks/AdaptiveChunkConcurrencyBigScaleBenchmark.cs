using SecureCloudBackup.Core.Services;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace SecureCloudBackup.Benchmarks;

/// <summary>
/// B27: re-run of the per-file chunk concurrency sweep
/// (<see cref="AdaptiveChunkConcurrencyBenchmark"/>) against the
/// big-scale workloads with the recommended 16 GB MemoryBudget. The
/// pre-B27 run found a -10% win at 12-way on
/// <c>realistic-large-200</c> -- but that workload capped file size
/// at 100 MB, which under the XXLargeChunkMaskBits path produces
/// only ~2 chunks per file. Two chunks x 12-way consumers is almost
/// entirely scheduler overhead, not real parallelism. This benchmark
/// re-runs the sweep on <c>media-library-500</c> (where 40% of files
/// are 200 MB-2 GB and produce 3-30 chunks each) to find out what
/// the real answer looks like in the regime the knob was designed
/// for.
///
/// <para>
/// <b>Extra axis.</b> Adds a 24-way point to the sweep. Pre-B27 only
/// went up to 12-way. With 16 GB budget the theoretical headroom
/// exists to test higher concurrency:
/// single-file worst case is 24 consumers x 64 MB x 2 = 3 GB per
/// file, well under the per-file budget slice when the orchestrator
/// spreads 16 GB across 8 file workers.
/// </para>
///
/// <para>
/// <b>What this benchmark resolves.</b>
/// <list type="bullet">
///   <item>If 12-way or 24-way wins decisively on
///     <c>media-library-500</c>, B27b (implement true adaptive chunk
///     concurrency) is clearly justified. The production change
///     scales concurrency inversely with per-file MaxChunkSize, same
///     pattern as <c>RestoreService.ComputeAdaptiveChunkConcurrency</c>.</item>
///   <item>If results are flat across the sweep, the per-file CDC
///     producer is the bottleneck (single-threaded on the read
///     side) and no number of consumers can help. B27b should NOT
///     ship.</item>
///   <item>Peak WS and StallCount together tell us whether the
///     16 GB budget is binding. If stalls are zero at every
///     configuration, the budget is not throttling; if 24-way shows
///     substantial stalls but 12-way does not, we have a defensible
///     upper bound.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Results (pending -- to be captured on this machine):</b>
/// <code>
/// // | Workload                | 4-way | 6-way | 12-way | 24-way | Best | Peak WS @best | StallCount @best |
/// // |------------------------ |-----: |-----: |------: |------: |----- |-------------: |----------------: |
/// // | media-library-500       |       |       |        |        |      |               |                  |
/// // | production-scale-3000   |       |       |        |        |      |               |                  |
/// // | huge-outlier-mixed      |       |       |        |        |      |               |                  |
/// </code>
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class AdaptiveChunkConcurrencyBigScaleBenchmark : BackupBenchmarkBase
{
    [ParamsSource(nameof(BigWorkloads))]
    public override string Workload { get; set; } = "media-library-500";

    // B27: 24-way was dropped after the 2026-04-24 run OOM-killed at
    // [ChunkConcurrency=24, Workload=media-library-500] with peak
    // working set = 58 GB. The 4/6/12 sweep already in the log shows
    // no monotone benefit beyond 6 on this hardware, so paying another
    // ~30 minutes of workload generation for a configuration we have
    // no theoretical reason to expect would suddenly win is wasteful.
    [Params(4, 6, 12)]
    public int ChunkConcurrency { get; set; }

    protected override int? MemoryLimitMBOverride => 16384;

    // B27: discard mode -- the InMemoryBlobService destination would
    // otherwise retain ~260 GB of ciphertext in process RAM during a
    // single iteration of media-library-500, and that destination
    // memory growth (not the orchestrator pipeline) was the real cause
    // of the OOM seen on the 2026-04-24 run.
    protected override bool RetainBlobPayloads => false;

    protected override void ConfigureOrchestrator(BackupOrchestrator orchestrator)
    {
        orchestrator.MaxParallelChunkUploadsOverride = ChunkConcurrency;
    }

    [Benchmark(Description = "Big-scale backup at parametric chunk concurrency")]
    public async Task Backup()
    {
        StartPeakWorkingSetCapture();
        try
        {
            await Orchestrator!.BackupFilesAsync(FilePaths);
        }
        finally
        {
            EmitPeakWorkingSet($"ChunkConcurrency={ChunkConcurrency}");
        }
    }
}
