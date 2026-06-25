using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace SecureCloudBackup.Benchmarks;

/// <summary>
/// B25-bench: end-to-end throughput baseline for the unmodified
/// backup pipeline. This is the regression detector -- if a future
/// change to CDC, encryption, or SqliteBackend slows the pipeline
/// down, the numbers in this benchmark move first.
///
/// <para>
/// <b>What it does NOT measure.</b> Real Azure egress, HTTP retry
/// behaviour, TLS handshake / connection-pool warmup, storage-account
/// throttling. Those are network-layer concerns that are constant
/// across any pipeline change in our code.
/// </para>
///
/// <para>
/// <b>Workload coverage.</b> Eight workloads across four size
/// profiles (uniform-1MB, mixed-realistic, large-skew, realistic-large).
/// See <see cref="BackupBenchmarkBase"/> for the per-profile
/// distributions and the disk-budget rationale for the asymmetric
/// FileCount values.
/// </para>
///
/// <para>
/// <b>Baseline numbers.</b> Updated whenever the harness changes or
/// the pipeline implementation changes materially. See the per-row
/// table at the end of this xmldoc.
/// </para>
///
/// <para>
/// <b>Cost.</b> Full matrix at default warmup/iteration counts is
/// roughly 30-45 minutes on the dev machine, dominated by the
/// realistic-large workloads.
/// </para>
///
/// <para>
/// <b>Baseline (captured 2026-04-24, B25-bench-2 commit
/// <c>HEAD</c>, no latency injection, warmupCount=1
/// iterationCount=2 invocationCount=1, hardware: Intel Core
/// i7-9700K @ 3.6 GHz Coffee Lake, 8 logical / 8 physical cores,
/// .NET 10.0.7, SQLite backend):</b>
/// <code>
/// // | Workload                |   Mean      |  Allocated   | Process Peak WS |
/// // |------------------------ |------------ |------------- |---------------- |
/// // | uniform-1MB-100         |    290 ms   |    207 MB    |    272 MB       |
/// // | uniform-1MB-1000        |  2,786 ms   |  2,067 MB    |  1,475 MB       |
/// // | mixed-realistic-100     |  1,572 ms   |    339 MB    |    428 MB       |
/// // | mixed-realistic-1000    |  9,500 ms   |  6,525 MB    |  6,346 MB       |
/// // | large-skew-100          |  4,941 ms   |  1,433 MB    |  2,047 MB       |
/// // | large-skew-200          |  5,525 ms   |  2,098 MB    |  2,047 MB       |
/// // | realistic-large-50      |  3,260 ms   |  2,796 MB    |  2,892 MB       |
/// // | realistic-large-200     | 15,179 ms   | 11,296 MB    | 11,194 MB       |
/// </code>
/// </para>
///
/// <para>
/// <b>Memory observations.</b> Process Peak WS reproduces the
/// production memory pressure documented in the 2026-04-23 logs
/// (memoryBudget=unlimited, multi-GB workingSet on a 7,313-file
/// backup):
/// <list type="bullet">
///   <item><c>realistic-large-200</c> hit 11.2 GB workingSet on a
///     200-file backup totalling roughly 12 GB on disk -- almost a
///     1:1 ratio of file-size-on-disk to in-flight memory. Production
///     at 7,313 files with 22 GB outliers under the same
///     memoryBudget=unlimited setting would scale catastrophically
///     worse.</item>
///   <item><c>mixed-realistic-1000</c> hit 6.3 GB workingSet on a
///     1000-file workload where 95% of files are under 10 MB. The
///     5% large bucket (50-200 MB) drives the peak.</item>
///   <item><c>large-skew-100</c> and <c>-200</c> both peak at the
///     same 2.0 GB because peak is set by the largest single file's
///     in-flight chunk buffers, not by file count.</item>
///   <item><c>uniform-1MB-1000</c> hit 1.5 GB on a 1 GB workload --
///     reasonable overhead, suggesting the per-file memory cost
///     scales roughly linearly with FileCount when chunks are small.</item>
/// </list>
/// These numbers justify <see cref="AdaptiveChunkConcurrencyBenchmark"/>
/// (lowering chunk concurrency for large-chunk workloads should
/// reduce peak by ~33%) and motivate caution about
/// <see cref="TwoTierFileSplitBenchmark"/> at 32-way file
/// concurrency (would multiply the per-worker memory cost across
/// many more concurrent workers).
/// </para>
///
/// <para>
/// <b>Throughput observations.</b>
/// <list type="bullet">
///   <item>The CPU-only floor is fast: 290 ms for 100 1 MB files,
///     scaling cleanly to 2,786 ms for 1000 -- a 9.6x slowdown for
///     10x more data, suggesting the pipeline is roughly
///     linear-in-bytes at this size class.</item>
///   <item><c>realistic-large-200</c> at 15.2 seconds dominated by
///     CDC + encryption of the large files. With production
///     CPU-at-3% telemetry, this means real Azure throughput is
///     latency-bound much more than CPU-bound -- the 15.2 second
///     CPU floor would be invisible behind multi-minute network
///     transfer time.</item>
///   <item><c>large-skew-200</c> is barely slower than
///     <c>large-skew-100</c> (5.5 vs 4.9 s) because both have the
///     same two ~500 MB outliers and the rest of the files are
///     small. Confirms the makespan-bound regime that
///     <see cref="LargestFirstSchedulingBenchmark"/> targets.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>B27 re-baseline (captured 2026-04-25, hardware: AMD EPYC 7763
/// @ 2.44 GHz, 16 logical / 8 physical cores in Hyper-V, .NET 10.0.6,
/// SQLite backend, warmupCount=1 iterationCount=2 invocationCount=1):</b>
/// <code>
/// // | Workload                |   Mean      |  Allocated   |
/// // |------------------------ |------------ |------------- |
/// // | uniform-1MB-100         |    277 ms   |    207 MB    |
/// // | uniform-1MB-1000        |  2,910 ms   |  2,068 MB    |
/// // | mixed-realistic-100     |  1,217 ms   |    340 MB    |
/// // | mixed-realistic-1000    |  7,371 ms   |  6,531 MB    |
/// // | large-skew-100          |  3,543 ms   |  1,434 MB    |
/// // | large-skew-200          |  4,100 ms   |  2,098 MB    |
/// // | realistic-large-50      |  2,724 ms   |  2,799 MB    |
/// // | realistic-large-200     | 12,346 ms   | 11,307 MB    |
/// </code>
/// Per AGENT_CONTEXT same-hardware discipline, do NOT compare these
/// numbers cell-by-cell against the i7-9700K block above; the absolute
/// timings differ for hardware reasons that have nothing to do with
/// the orchestrator pipeline. The qualitative shape is the same: the
/// pipeline is roughly linear-in-bytes at the 100-1000 file scale,
/// the realistic-large workloads dominate the wall clock, and CPU
/// pressure is modest. This block is the authoritative comparison
/// point for any future re-run on this same machine.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class BackupThroughputBenchmark : BackupBenchmarkBase
{
    [ParamsSource(nameof(AllWorkloads))]
    public override string Workload { get; set; } = "uniform-1MB-100";

    [Benchmark(Description = "End-to-end backup, unmodified orchestrator")]
    public async Task Backup()
    {
        StartPeakWorkingSetCapture();
        try
        {
            await Orchestrator!.BackupFilesAsync(FilePaths);
        }
        finally
        {
            EmitPeakWorkingSet("Backup");
        }
    }
}
