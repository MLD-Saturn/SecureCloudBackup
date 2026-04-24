using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace AzureBackup.Benchmarks;

/// <summary>
/// B25-bench-2 / Benchmark A: does sorting the file list by size DESC
/// before the file-level <c>Parallel.ForEachAsync</c> reduce wall-clock?
///
/// <para>
/// <b>Why this is worth measuring.</b> Longest Processing Time (LPT)
/// scheduling is a classic makespan heuristic: when N tasks of varying
/// duration are dispatched across W workers, starting the longest tasks
/// first reduces the makespan upper bound from O(P_max) +
/// O(P_total / W) to roughly O(P_max). The current
/// <c>BackupFilesCoreAsync</c> dispatches files in input order, so a
/// 22 GB MKV that happens to land late in the list leaves W-1 workers
/// idle while it finishes. LPT would put it first.
/// </para>
///
/// <para>
/// <b>What this benchmark does.</b> Two variants run against an
/// otherwise-unmodified orchestrator:
/// <list type="bullet">
///   <item><c>input-order</c>: pass the file list as-is. Matches
///     today's production behaviour.</item>
///   <item><c>largest-first</c>: sort by size DESC before passing.
///     Implements LPT.</item>
/// </list>
/// No production code change is needed -- the benchmark just permutes
/// the input list. If <c>largest-first</c> is consistently faster, the
/// follow-up production change is a one-line <c>OrderByDescending</c>
/// in <c>BackupFilesCoreAsync</c>.
/// </para>
///
/// <para>
/// <b>What we expect.</b> Meaningful improvement only on workloads
/// with high size variance:
/// <list type="bullet">
///   <item><c>uniform-1MB-100/1000</c>: provably 0% (all files equal).
///     Acts as a regression detector.</item>
///   <item><c>mixed-realistic-100/1000</c>: small expected delta
///     (5% of files are large; their ordering matters).</item>
///   <item><c>large-skew-100/200</c>: largest expected win. Two ~750 MB
///     outliers dominate the makespan; starting them first lets the
///     other workers finish all small files before either outlier
///     completes, so the total makespan compresses to roughly the
///     time of the slower outlier alone.</item>
///   <item><c>realistic-large-50/200</c>: modest expected win. Most
///     files are 50-500 MB so size variance is bounded; LPT mostly
///     helps if the largest files happen to land late in the input
///     order.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Results table (captured 2026-04-24, B25-bench-2 commit
/// <c>HEAD</c>, hardware: Intel Core i7-9700K @ 3.6 GHz Coffee Lake,
/// 8 logical / 8 physical cores, .NET 10.0.7, SQLite backend,
/// warmupCount=1 iterationCount=1):</b>
/// <code>
/// // | Workload                | Input-order | Largest-first | Delta   |
/// // |------------------------ |-----------: |-------------: |-------: |
/// // | uniform-1MB-100         |    282 ms   |     271 ms    |  -4%    |
/// // | uniform-1MB-1000        |  2,831 ms   |   2,831 ms    |   0%    |
/// // | mixed-realistic-100     |  1,612 ms   |   1,566 ms    |  -3%    |
/// // | mixed-realistic-1000    |  8,575 ms   |  10,038 ms    | +17% !  |
/// // | large-skew-100          |  5,001 ms   |   4,221 ms    | -16%    |
/// // | large-skew-200          |  5,276 ms   |   3,977 ms    | -25%    |
/// // | realistic-large-50      |  3,314 ms   |   3,306 ms    |   0%    |
/// // | realistic-large-200     | 13,930 ms   |  16,045 ms    | +15% !  |
/// </code>
/// </para>
///
/// <para>
/// <b>Conclusion: LPT scheduling is NOT a unilateral win and would
/// REGRESS the most common workloads.</b>
/// <list type="bullet">
///   <item><c>large-skew</c> behaves as theory predicts: starting
///     the two ~500 MB outliers first compresses the makespan by
///     16-25%. This is the textbook LPT win.</item>
///   <item><c>mixed-realistic-1000</c> and <c>realistic-large-200</c>
///     REGRESS by 15-17%. Hypothesis: with input-order, large and
///     small files interleave so when a large file finishes a
///     worker, several small files are still in flight to keep the
///     SqliteBackend write lock contended at a steady rate and the
///     channel pipeline warm. Largest-first front-loads all the
///     long files, then the remaining 7 workers race through small
///     files in parallel and ALL 7 go idle waiting for the large
///     files to finish -- losing the steady-state pipelining
///     benefit. This is the OPPOSITE of LPT theory and indicates
///     the production bottleneck on this code path is NOT a
///     single-worker makespan but something steady-state about
///     the SqliteBackend write lock or the CDC startup cost
///     amortisation.</item>
///   <item><c>uniform-1MB-*</c> see no change as expected (all files
///     same size).</item>
///   <item><c>realistic-large-50</c> sees no change because at 50
///     files even the input-order arrangement has the larger files
///     starting reasonably early statistically.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Recommendation derived from these numbers</b>: do NOT ship a
/// blanket largest-first sort in BackupFilesCoreAsync. A workload-
/// aware variant ("sort only when size variance exceeds N") might
/// recover the large-skew win without the mixed-realistic
/// regression, but is much more complex and would need its own
/// benchmark sweep before being shipped. The simpler honest answer
/// is to leave the production scheduling alone until we understand
/// WHY LPT regresses these workloads.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 2, invocationCount: 1)]
public class LargestFirstSchedulingBenchmark : BackupBenchmarkBase
{
    [ParamsSource(nameof(AllWorkloads))]
    public override string Workload { get; set; } = "uniform-1MB-100";

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
        // Sort by file size descending. The sort itself is O(N log N)
        // and adds at most a few ms to the per-iteration cost; the
        // sorted list is materialised so the cost is paid once per
        // iteration, not once per file dispatch.
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
