using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace AzureBackup.Benchmarks;

/// <summary>
/// B25-bench-2 / Benchmark C: does varying per-file chunk concurrency
/// affect throughput AND memory pressure?
///
/// <para>
/// <b>Why this is worth measuring.</b> The restore service uses
/// adaptive per-file chunk concurrency that scales inversely with max
/// chunk size: large chunks (64 MB) get fewer in-flight downloads,
/// small chunks (256 KB) get more. Backup uses a fixed
/// MaxParallelChunkUploads = 6 regardless of chunk size. For the
/// realistic-large workload with .mkv files using XXLargeChunkMaskBits
/// (max chunk = 64 MB), 6 in-flight chunks at 2x plaintext each =
/// 768 MB per file. At 8 file-level workers that's potentially 6 GB
/// of in-flight chunk buffers -- consistent with the multi-GB
/// workingSet observed in production with memoryBudget=unlimited.
/// </para>
///
/// <para>
/// <b>What this benchmark does.</b> Sweeps per-file chunk concurrency
/// across {6 (today), 4, 12}. Lower than 6 is interesting because it
/// directly reduces peak memory at the cost of throughput; higher
/// than 6 tests whether more parallelism per file actually helps
/// (production CPU was at 3%, so there's parallelism budget if the
/// pipeline can use it).
/// </para>
///
/// <para>
/// <b>Memory expectations.</b> The benchmark explicitly reports peak
/// workingSet because the throughput delta alone can be misleading:
/// a 12-way config that runs 5% faster but holds 50% more memory is
/// a bad trade for the user who has a 2 GB memory limit configured.
/// The benchmark numbers should make this trade-off explicit.
/// </para>
///
/// <para>
/// <b>What we expect under the "CPU is 3%" reality.</b>
/// <list type="bullet">
///   <item><c>4-way</c>: should reduce peak memory by ~33% on
///     workloads with large chunks (realistic-large, large-skew),
///     possibly with a small throughput cost. If throughput is
///     unchanged or improves, today's 6 is over-parallelised.</item>
///   <item><c>12-way</c>: doubles the in-flight chunk count. If the
///     bottleneck is the per-file CDC producer (single-threaded),
///     raising the consumer count buys nothing and just uses more
///     memory. If the bottleneck is anything downstream of the
///     channel, more consumers should help.</item>
///   <item>For workloads with small chunks (uniform-1MB,
///     mixed-realistic), the per-file memory impact is small in
///     absolute terms but the throughput delta should reveal
///     whether 6 is the right number for those workloads too.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Results table (captured 2026-04-24, B25-bench-2 commit
/// <c>HEAD</c>, hardware: Intel Core i7-9700K @ 3.6 GHz Coffee Lake,
/// 8 logical / 8 physical cores, .NET 10.0.7, SQLite backend,
/// warmupCount=0 iterationCount=1 -- expect ~5% noise per row):</b>
/// <code>
/// // | Workload                | 4-way Mean  | 6-way Mean  | 12-way Mean | Best |
/// // |------------------------ |-----------: |-----------: |-----------: |----- |
/// // | uniform-1MB-100         |    284 ms   |    278 ms   |    298 ms   |  6   |
/// // | uniform-1MB-1000        |  2,990 ms   |  2,824 ms   |  2,909 ms   |  6   |
/// // | mixed-realistic-100     |  1,599 ms   |  1,765 ms   |  1,630 ms   |  4   |
/// // | mixed-realistic-1000    |  8,540 ms   |  9,433 ms   |  9,665 ms   |  4 (-9% vs today) |
/// // | large-skew-100          |  4,962 ms   |  4,943 ms   |  4,978 ms   |  flat |
/// // | large-skew-200          |  5,403 ms   |  5,517 ms   |  5,564 ms   |  4   |
/// // | realistic-large-50      |  3,603 ms   |  3,350 ms   |  3,512 ms   |  6   |
/// // | realistic-large-200     | 14,869 ms   | 15,699 ms   | 14,120 ms   | 12 (-10% vs today) |
/// </code>
/// </para>
///
/// <para>
/// <b>Conclusion: chunk concurrency is workload-sensitive but the
/// production default of 6 is close to optimal on most workloads.</b>
/// <list type="bullet">
///   <item><c>mixed-realistic-1000</c> wins -9% at 4-way. Hypothesis:
///     this workload has many small files (95% under 10 MB) that
///     produce few chunks each; over-parallelising the per-file
///     consumer count just adds Channel scheduling overhead with
///     no meaningful concurrency gain.</item>
///   <item><c>realistic-large-200</c> wins -10% at 12-way. Opposite
///     story: large files with many large chunks benefit from more
///     concurrent chunk uploads keeping the channel warm.</item>
///   <item><c>uniform-1MB-*</c> and <c>large-skew-100</c> are flat
///     across all three settings (within run-to-run noise of ~5%).</item>
///   <item>Memory cost across settings is uniform (peak workingSet
///     barely moves) because the MemoryBudget is unlimited and
///     the chunk buffers come from ArrayPool which reuses across
///     concurrency levels.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Recommendation derived from these numbers</b>: do NOT change
/// the production fixed value of 6 unilaterally. Either:
/// <list type="number">
///   <item>Leave it at 6 (status quo) -- the wins from changing it
///     are modest (-9% / -10%) and require workload-specific
///     tuning to capture, with regressions on other workloads.</item>
///   <item>Implement TRUE adaptive concurrency in
///     <c>BackupOrchestrator.BackupFileAsync</c> that mirrors
///     restore's <c>ComputeAdaptiveChunkConcurrency</c>: read the
///     per-file <c>config.MaxChunkSize</c> and scale concurrency
///     inversely (small chunks -> more concurrent, large chunks
///     -> fewer). This recovers BOTH wins because the per-file
///     decision matches the per-file workload. The implementation
///     is ~20 LOC and has a precedent (RestoreService line 98).</item>
/// </list>
/// Option 2 is the right answer if the wins matter. Re-run THIS
/// benchmark with the adaptive logic in place to confirm both
/// the mixed-realistic-1000 and realistic-large-200 wins survive.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class AdaptiveChunkConcurrencyBenchmark : BackupBenchmarkBase
{
    [ParamsSource(nameof(AllWorkloads))]
    public override string Workload { get; set; } = "uniform-1MB-100";

    /// <summary>
    /// Per-file chunk-upload concurrency under test. Production
    /// default is 6; lower values trade throughput for memory; higher
    /// values bet that downstream parallelism slack exists.
    /// </summary>
    [Params(4, 6, 12)]
    public int ChunkConcurrency { get; set; }

    protected override void ConfigureOrchestrator(BackupOrchestrator orchestrator)
    {
        orchestrator.MaxParallelChunkUploadsOverride = ChunkConcurrency;
    }

    [Benchmark(Description = "End-to-end backup at parametric chunk concurrency")]
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
