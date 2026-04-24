using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;

namespace AzureBackup.Benchmarks;

/// <summary>
/// B25-bench-2 shared harness for the four backup benchmarks
/// (<see cref="BackupThroughputBenchmark"/> and the three
/// design-decision benchmarks: <see cref="LargestFirstSchedulingBenchmark"/>,
/// <see cref="TwoTierFileSplitBenchmark"/>,
/// <see cref="AdaptiveChunkConcurrencyBenchmark"/>).
///
/// <para>
/// <b>Why a base class.</b> All four benchmarks need the same setup
/// (synthetic file workload, fresh DB + encryption + blob service per
/// iteration, peak workingSet capture, disposal in the right order)
/// and the same workload definitions. Putting that in one place keeps
/// the four subclasses focused on the variable they vary, and prevents
/// accidental drift between them when the shared setup changes.
/// </para>
///
/// <para>
/// <b>Key design choices.</b>
/// <list type="bullet">
///   <item><b>No simulated network latency.</b> An earlier draft of the
///     throughput benchmark swept <c>SimulatedLatencyMs</c> over
///     {0, 50}. Production telemetry showed CPU sat below 3% on a
///     7,313-file backup against real Azure, which means the
///     production bottleneck is not CPU and not network bandwidth --
///     it is something serial elsewhere in the pipeline (disk, the
///     SqliteBackend write lock, or HttpClient state). Adding fake
///     latency to a benchmark would shift every measurement right
///     by the same constant; the *delta* between two design
///     decisions is unchanged. Latency injection is dropped to keep
///     run time low and the signal clean.</item>
///   <item><b>Workloads are bundled (profile, fileCount) tuples</b>
///     rather than a cross product. Disk space is the binding
///     constraint (the dev machine has ~24 GB free and the
///     <c>realistic-large</c> profile at 1000 files would exceed
///     that); bundling lets each subclass declare exactly the
///     combinations that fit.</item>
///   <item><b>Peak workingSet captured per iteration.</b>
///     <c>[MemoryDiagnoser]</c> reports cumulative managed allocations
///     ("Allocated"), not peak resident bytes. The two-tier and
///     adaptive-chunk benchmarks need peak resident to detect when
///     a "faster" config crossed an OOM cliff (production was
///     observed running with memoryBudget=unlimited and held
///     multi-GB workingSet on 7,313-file backups). Subclasses call
///     <see cref="StartPeakWorkingSetCapture"/> at the start of the
///     benchmark method and <see cref="EmitPeakWorkingSet"/> at the
///     end; values land in the BDN console output for post-run
///     extraction.</item>
///   <item><b>Synthesises the encryption key directly</b> via
///     <see cref="EncryptionService.Initialize(byte[])"/> instead
///     of running Argon2id KDF, saving ~1-2 seconds per
///     IterationSetup. SQLite still derives its own key per
///     <see cref="LocalDatabaseService.Initialize(string, string)"/>;
///     that's accepted as fixed setup overhead.</item>
/// </list>
/// </para>
/// </summary>
public abstract class BackupBenchmarkBase
{
    /// <summary>
    /// Bundled (size profile, file count) tuple per workload. See
    /// <see cref="GetWorkload"/> for the mapping. A single Workload
    /// param keeps the matrix asymmetric across profiles, which is
    /// necessary because <c>realistic-large</c> at 1000 files would
    /// blow the dev machine's disk budget.
    /// </summary>
    public abstract string Workload { get; set; }

    private string _testDir = string.Empty;
    private string _filesDir = string.Empty;
    private string _dbPath = string.Empty;
    private byte[] _derivedKey = [];
    private long _peakWorkingSetBaselineBytes;

    /// <summary>
    /// File paths for the current Workload, in input order. Subclasses
    /// may permute this in their own benchmark methods (e.g.
    /// <see cref="LargestFirstSchedulingBenchmark"/> sorts by size).
    /// </summary>
    protected List<string> FilePaths { get; private set; } = [];

    /// <summary>Per-iteration backup orchestrator. Recreated each iteration.</summary>
    protected BackupOrchestrator? Orchestrator { get; private set; }

    private LocalDatabaseService? _databaseService;
    private EncryptionService? _encryptionService;
    private ChunkingService? _chunkingService;
    private FileWatcherService? _fileWatcherService;
    private InMemoryBlobService? _blobService;

    [GlobalSetup]
    public void BaseGlobalSetup()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string what) => Console.WriteLine(
            $"[setup T={sw.ElapsedMilliseconds,6} ms] [workload={Workload}] {what}");

        // Defence: previous benchmark runs that crashed mid-IterationSetup
        // (e.g. ran out of disk) may have leaked their azbk-backup-bench-*
        // directories. Before doing anything else, clean them up so we
        // don't compound the disk pressure across runs.
        try
        {
            var tempRoot = Path.GetTempPath();
            foreach (var dir in Directory.GetDirectories(tempRoot, "azbk-backup-bench-*"))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }

        Mark("creating temp dirs");
        _testDir = Path.Combine(Path.GetTempPath(),
            "azbk-backup-bench-" + Guid.NewGuid().ToString("N"));
        _filesDir = Path.Combine(_testDir, "files");
        Directory.CreateDirectory(_filesDir);
        _dbPath = Path.Combine(_testDir, "backup.db");

        // Force the SQLite backend regardless of any process-level env var.
        // SQLite is the production default since C-5.
        Environment.SetEnvironmentVariable("AZBK_USE_SQLITE", "1");

        var (profile, fileCount) = GetWorkload(Workload);

        // B27 disk-space preflight. The original B25-bench-2 runs
        // crashed mid-IterationSetup when the dev machine ran out of
        // disk (it had ~28 GB free and the realistic-large profile
        // at 1000 files would have exceeded that). Big-scale workloads
        // are explicitly allowed to exceed the original budget; fail
        // fast with a clear error rather than crashing later.
        var estimatedBytes = EstimateWorkloadBytes(Workload);
        if (estimatedBytes > 0)
        {
            var drive = new DriveInfo(Path.GetPathRoot(_filesDir) ?? "C:\\");
            var free = drive.AvailableFreeSpace;
            var required = (long)(estimatedBytes * 1.5);
            Mark($"disk preflight: free={free / 1024.0 / 1024 / 1024:N1} GB, " +
                 $"estimated workload={estimatedBytes / 1024.0 / 1024 / 1024:N1} GB, " +
                 $"required (1.5x)={required / 1024.0 / 1024 / 1024:N1} GB");
            if (free < required)
            {
                throw new InvalidOperationException(
                    $"Insufficient disk space for workload '{Workload}'. " +
                    $"Free: {free / 1024.0 / 1024 / 1024:N1} GB on {drive.Name}, " +
                    $"required (estimated workload x 1.5): {required / 1024.0 / 1024 / 1024:N1} GB.");
            }
        }

        Mark($"generating workload: profile={profile}, files={fileCount}");
        FilePaths = GenerateFiles(_filesDir, fileCount, profile);
        var totalBytes = FilePaths.Sum(p => new FileInfo(p).Length);
        Mark($"workload ready: {fileCount} files, {totalBytes:N0} bytes total ({totalBytes / 1024.0 / 1024:N1} MB)");

        _derivedKey = new byte[32];
        new Random(42).NextBytes(_derivedKey);

        Mark("base global setup complete");
    }

    [IterationSetup]
    public void BaseIterationSetup()
    {
        DisposeIterationServices();
        TryDeleteDatabaseArtifacts();

        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, "BenchmarkPassword123!");

        // Apply the per-iteration MemoryBudget override (B27 seam).
        // Production telemetry from 2026-04-23 showed the user running
        // with memoryBudget=unlimited; the big-scale benchmarks need
        // to simulate the recommended 16 GB production setting so the
        // new numbers reflect what users will actually experience.
        // The orchestrator reads GetConfiguration() at the top of
        // BackupFilesCoreAsync, so updating the row here is sufficient.
        // A null override preserves the historical unlimited-budget
        // behaviour so the pre-B27 small-workload results remain
        // directly comparable.
        var mbOverride = MemoryLimitMBOverride;
        if (mbOverride is int mb)
        {
            var cfg = _databaseService.GetConfiguration();
            cfg.MemoryLimitEnabled = true;
            cfg.MemoryLimitMB = mb;
            _databaseService.SaveConfiguration(cfg);
        }

        _encryptionService = new EncryptionService();
        _encryptionService.Initialize(_derivedKey);

        _chunkingService = new ChunkingService();
        _fileWatcherService = new FileWatcherService(_databaseService);
        _blobService = new InMemoryBlobService(_encryptionService);

        _blobService.ConnectAsync("UseDevelopmentStorage=true", "bench").GetAwaiter().GetResult();

        Orchestrator = new BackupOrchestrator(
            _databaseService, _encryptionService, _chunkingService,
            _blobService, _fileWatcherService);

        // Subclasses may need to apply per-iteration overrides (e.g.
        // setting MaxParallelFileBackupsOverride for the two-tier
        // benchmark). Hook for them.
        ConfigureOrchestrator(Orchestrator);
    }

    /// <summary>
    /// Optional MemoryBudget setting applied via
    /// <see cref="LocalDatabaseService.SaveConfiguration"/> at the top
    /// of every iteration. Default <c>null</c> preserves the original
    /// B25-bench-2 behaviour of unlimited MemoryBudget so the published
    /// small-workload result tables remain directly comparable.
    /// Big-scale benchmarks (see <c>ProductionScaleBackupBenchmark</c>
    /// and the <c>*BigScaleBenchmark</c> classes) return 16384 to mirror
    /// the recommended production memory limit; the dedicated
    /// <c>MemoryBudgetBenchmark</c> sweeps the value parametrically.
    /// </summary>
    protected virtual int? MemoryLimitMBOverride => null;

    /// <summary>
    /// Hook for subclasses to apply per-iteration orchestrator
    /// configuration before the timed benchmark begins. Default
    /// implementation is a no-op (used by
    /// <see cref="BackupThroughputBenchmark"/> and
    /// <see cref="LargestFirstSchedulingBenchmark"/> which both
    /// run against the unmodified orchestrator).
    /// </summary>
    protected virtual void ConfigureOrchestrator(BackupOrchestrator orchestrator) { }

    [IterationCleanup]
    public void BaseIterationCleanup()
    {
        DisposeIterationServices();
    }

    [GlobalCleanup]
    public void BaseGlobalCleanup()
    {
        DisposeIterationServices();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Snapshots the process's working set at the start of a benchmark
    /// method so <see cref="EmitPeakWorkingSet"/> can later report a
    /// per-iteration delta. The OS PeakWorkingSet64 is monotonic over
    /// the process lifetime, so we have to subtract a baseline to get
    /// per-iteration high-water marks. Refresh() forces an updated
    /// snapshot; without it the value is stale to the last GC.
    /// </summary>
    protected void StartPeakWorkingSetCapture()
    {
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        // ResetPeakWorkingSet on Windows zeros the OS-tracked peak so
        // PeakWorkingSet64 after this call reflects only the upcoming
        // iteration. On non-Windows this property is not implemented
        // and we fall back to a baseline-subtraction approach.
        try
        {
            // No public API, so we baseline by reading current peak
            // and subtracting in EmitPeakWorkingSet. This is correct
            // even when ResetPeakWorkingSet is unavailable.
            proc.Refresh();
            _peakWorkingSetBaselineBytes = proc.PeakWorkingSet64;
        }
        catch
        {
            _peakWorkingSetBaselineBytes = 0;
        }
    }

    /// <summary>
    /// Emits the peak workingSet observed during the current iteration
    /// to the BDN console output. Format is parseable for post-run
    /// extraction:
    /// <code>
    /// [peakWS workload=&lt;name&gt; iteration=&lt;n&gt;] peak=&lt;bytes&gt; deltaFromBaseline=&lt;bytes&gt;
    /// </code>
    /// </summary>
    protected void EmitPeakWorkingSet(string label)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            proc.Refresh();
            var peak = proc.PeakWorkingSet64;
            var delta = Math.Max(0, peak - _peakWorkingSetBaselineBytes);
            Console.WriteLine(
                $"[peakWS workload={Workload} label={label}] peak={peak:N0} bytes ({peak / 1024.0 / 1024:N1} MB), " +
                $"deltaFromBaseline={delta:N0} bytes ({delta / 1024.0 / 1024:N1} MB)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[peakWS workload={Workload} label={label}] capture failed: {ex.Message}");
        }
    }

    private void DisposeIterationServices()
    {
        try { Orchestrator = null; } catch { }
        try { _blobService = null; } catch { }
        try { _fileWatcherService?.Dispose(); _fileWatcherService = null; } catch { }
        try { _chunkingService = null; } catch { }
        try { _encryptionService?.Dispose(); _encryptionService = null; } catch { }
        try { _databaseService?.Dispose(); _databaseService = null; } catch { }
    }

    private void TryDeleteDatabaseArtifacts()
    {
        foreach (var ext in new[] { "", "-wal", "-shm", ".salt" })
        {
            try
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Decodes the bundled Workload string into (profile, fileCount).
    /// Bundled rather than cross-product because not every (profile,
    /// fileCount) combination fits on the dev machine's disk -- see
    /// the class summary for the disk budget rationale.
    /// </summary>
    protected static (string profile, int fileCount) GetWorkload(string workload) => workload switch
    {
        "uniform-1MB-100" => ("uniform-1MB", 100),
        "uniform-1MB-1000" => ("uniform-1MB", 1000),
        "mixed-realistic-100" => ("mixed-realistic", 100),
        "mixed-realistic-1000" => ("mixed-realistic", 1000),
        "large-skew-100" => ("large-skew", 100),
        "large-skew-200" => ("large-skew", 200),
        "realistic-large-50" => ("realistic-large", 50),
        "realistic-large-200" => ("realistic-large", 200),
        // B27 big-scale workloads. Gated by the disk preflight above;
        // these are deliberately larger than the dev machine's previous
        // ~28 GB disk budget and are the workloads actually required to
        // answer the design questions at real production scale.
        "media-library-500" => ("media-library", 500),
        "production-scale-3000" => ("production-scale", 3000),
        "huge-outlier-mixed" => ("huge-outlier", 500),
        _ => throw new ArgumentException($"Unknown workload: {workload}", nameof(workload)),
    };

    /// <summary>
    /// All original (pre-B27) small workloads. Subclasses that want the
    /// full matrix declare <c>[ParamsSource(nameof(AllWorkloads))]</c>;
    /// subclasses that want a subset declare an explicit
    /// <c>[Params(...)]</c>. Does NOT include the big-scale workloads
    /// added in B27, which live in <see cref="BigWorkloads"/>.
    /// </summary>
    public static IEnumerable<string> AllWorkloads =>
    [
        "uniform-1MB-100",
        "uniform-1MB-1000",
        "mixed-realistic-100",
        "mixed-realistic-1000",
        "large-skew-100",
        "large-skew-200",
        "realistic-large-50",
        "realistic-large-200",
    ];

    /// <summary>
    /// B27 big-scale workloads designed to reproduce the user's real
    /// 2026-04-23 production workload shape. Each requires substantial
    /// free disk (see <c>EstimateWorkloadBytes</c> for rough sizes)
    /// and is not included in <see cref="AllWorkloads"/> so the pre-B27
    /// benchmarks remain directly comparable to their historical
    /// result tables.
    /// <list type="bullet">
    ///   <item><c>media-library-500</c>: 500 files, 10% under 10 MB,
    ///     50% in 50-200 MB, 40% in 200 MB-2 GB. ~260 GB on disk.
    ///     Exercises the many-chunks-per-file regime that the fixed
    ///     100 MB cap in <c>realistic-large</c> could not reach.</item>
    ///   <item><c>production-scale-3000</c>: 3,000 files mirroring the
    ///     shape of the 2026-04-23 production log scaled down from
    ///     7,313 files. ~70 GB on disk. Exercises the SqliteBackend
    ///     write lock under realistic file count pressure.</item>
    ///   <item><c>huge-outlier-mixed</c>: 498 small files plus one
    ///     10 GB file and one 20 GB file. ~32 GB on disk. The textbook
    ///     LPT case -- the two outliers dominate the makespan by a
    ///     large margin, so largest-first ought to compress total
    ///     runtime decisively. If it does not, the W2 hypothesis
    ///     (production scheduling is bound by steady-state pipelining,
    ///     not makespan) is refuted.</item>
    /// </list>
    /// </summary>
    public static IEnumerable<string> BigWorkloads =>
    [
        "media-library-500",
        "production-scale-3000",
        "huge-outlier-mixed",
    ];

    /// <summary>
    /// Rough expected-bytes-on-disk for the big-scale workloads, used
    /// by the disk preflight in <see cref="BaseGlobalSetup"/>. Returns
    /// 0 for the pre-B27 small workloads (no preflight needed -- they
    /// fit in single-digit GB and the preflight is noise). Numbers are
    /// conservative overestimates; the preflight multiplies by 1.5
    /// again to cover filesystem overhead and the DB + blob-service
    /// working state that accumulates during the run.
    /// </summary>
    protected static long EstimateWorkloadBytes(string workload) => workload switch
    {
        // Pre-B27 small workloads: no preflight.
        "uniform-1MB-100" or "uniform-1MB-1000" or
        "mixed-realistic-100" or "mixed-realistic-1000" or
        "large-skew-100" or "large-skew-200" or
        "realistic-large-50" or "realistic-large-200" => 0,
        // Big-scale workloads: conservative overestimates in bytes.
        "media-library-500" => 260L * 1024 * 1024 * 1024,
        "production-scale-3000" => 80L * 1024 * 1024 * 1024,
        "huge-outlier-mixed" => 35L * 1024 * 1024 * 1024,
        _ => 0,
    };

    private static List<string> GenerateFiles(string dir, int count, string profile)
    {
        // Deterministic seed so a re-run produces identical workload bytes
        // (CDC chunk boundaries + dedup behaviour stay reproducible).
        var rng = new Random(42);
        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var size = NextSize(profile, i, count, rng);
            var path = Path.Combine(dir, $"file_{i:D5}_{size}.bin");
            // Random bytes so CDC sees a realistic boundary distribution.
            // Stream the random bytes in 1 MB chunks so we don't need to
            // allocate a single multi-GB byte[] for the realistic-large
            // profile's 500 MB files.
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            const int writeChunk = 1024 * 1024;
            var buffer = new byte[writeChunk];
            var remaining = size;
            while (remaining > 0)
            {
                var thisWrite = (int)Math.Min(writeChunk, remaining);
                rng.NextBytes(buffer.AsSpan(0, thisWrite));
                fs.Write(buffer, 0, thisWrite);
                remaining -= thisWrite;
            }
            paths.Add(path);
        }
        return paths;
    }

    private static long NextSize(string profile, int index, int count, Random rng) => profile switch
    {
        "uniform-1MB" => 1024 * 1024,
        "mixed-realistic" => MixedRealisticSize(rng),
        "large-skew" => LargeSkewSize(index, count, rng),
        "realistic-large" => RealisticLargeSize(index, count, rng),
        // B27 big-scale profiles.
        "media-library" => MediaLibrarySize(rng),
        "production-scale" => ProductionScaleSize(rng),
        "huge-outlier" => HugeOutlierSize(index, count, rng),
        _ => throw new ArgumentException($"Unknown size profile: {profile}", nameof(profile)),
    };

    private static long MixedRealisticSize(Random rng)
    {
        // 80% small (8 KB - 256 KB), 15% medium (1-10 MB), 5% large (50-200 MB).
        var bucket = rng.NextDouble();
        if (bucket < 0.80) return rng.Next(8 * 1024, 256 * 1024);
        if (bucket < 0.95) return rng.Next(1 * 1024 * 1024, 10 * 1024 * 1024);
        return rng.Next(50 * 1024 * 1024, 200 * 1024 * 1024);
    }

    private static long LargeSkewSize(int index, int count, Random rng)
    {
        // Two outliers (the last two indexes) at 250-500 MB; everyone else
        // 1-10 MB. The outliers dominate the makespan for small file counts.
        // Capped at 500 MB (was 1 GB) to keep the 200-file workload under
        // ~5 GB total -- the dev machine only has ~28 GB free at peak.
        if (index >= count - 2) return rng.Next(250 * 1024 * 1024, 500 * 1024 * 1024);
        return rng.Next(1 * 1024 * 1024, 10 * 1024 * 1024);
    }

    private static long RealisticLargeSize(int index, int count, Random rng)
    {
        // Mimics the user's actual production workload (logs from 2026-04-23
        // showed 7,313 files with hundreds in the 100-500 MB range plus 29
        // files over 500 MB and outliers up to 22 GB). The benchmark caps
        // file size at 100 MB -- not the 500 MB or 22 GB seen in production --
        // because the dev machine only has ~28 GB free at peak and a 200-file
        // workload at 500 MB average would not fit. The memory-pressure
        // pattern still reproduces at 100 MB: chunker uses 1-64 MB chunks
        // for media extensions (XXLargeChunkMaskBits) regardless of file
        // size, so a 100 MB file still produces multiple 4-16 MB chunks
        // and exercises the in-flight chunk buffer ceiling.
        // Distribution at this scaled size:
        //   60% in 30-70 MB (the documented "video files" bucket, scaled)
        //   30% in 70-100 MB (larger media, scaled)
        //   10% small files (<5 MB) for some heterogeneity
        var bucket = rng.NextDouble();
        if (bucket < 0.10) return rng.Next(64 * 1024, 5 * 1024 * 1024);
        if (bucket < 0.70) return rng.Next(30 * 1024 * 1024, 70 * 1024 * 1024);
        return rng.Next(70 * 1024 * 1024, 100 * 1024 * 1024);
    }

    /// <summary>
    /// B27 media-library profile. 500 files total, designed to
    /// exercise the many-chunks-per-file regime that the pre-B27
    /// <c>realistic-large</c> profile could not reach because it was
    /// capped at 100 MB. At 200 MB-2 GB the chunker produces 3-30
    /// chunks per file under the XXLargeChunkMaskBits path
    /// (1-64 MB chunks for media extensions), which is the only
    /// regime where per-file chunk concurrency is a meaningful knob.
    /// Distribution:
    ///   10% small (10 KB - 10 MB) for some heterogeneity
    ///   50% medium (50-200 MB)
    ///   40% large (200 MB - 2 GB)
    /// Expected total: ~260 GB on disk.
    /// </summary>
    private static long MediaLibrarySize(Random rng)
    {
        var bucket = rng.NextDouble();
        if (bucket < 0.10) return rng.NextInt64(10L * 1024, 10L * 1024 * 1024);
        if (bucket < 0.60) return rng.NextInt64(50L * 1024 * 1024, 200L * 1024 * 1024);
        return rng.NextInt64(200L * 1024 * 1024, 2L * 1024 * 1024 * 1024);
    }

    /// <summary>
    /// B27 production-scale profile. Mirrors the shape of the
    /// 2026-04-23 production log (7,313 files total) scaled down
    /// proportionally to 3,000 files to keep run time bounded.
    /// Shape derived from the log:
    ///   89% small (&lt; 5 MB, typically photos, documents, small logs)
    ///   9% medium (5-100 MB, typical office / source / archive files)
    ///   1.4% large (100-500 MB, videos, VM images, large archives)
    ///   0.4% very large (500 MB - 5 GB, long-form video, database dumps)
    /// Expected total: ~70 GB on disk. The SqliteBackend write lock
    /// is the suspected production bottleneck; 3,000 files is the
    /// right scale to reveal whether the lock saturates.
    /// </summary>
    private static long ProductionScaleSize(Random rng)
    {
        var bucket = rng.NextDouble();
        if (bucket < 0.89) return rng.NextInt64(4L * 1024, 5L * 1024 * 1024);
        if (bucket < 0.98) return rng.NextInt64(5L * 1024 * 1024, 100L * 1024 * 1024);
        if (bucket < 0.996) return rng.NextInt64(100L * 1024 * 1024, 500L * 1024 * 1024);
        return rng.NextInt64(500L * 1024 * 1024, 5L * 1024 * 1024 * 1024);
    }

    /// <summary>
    /// B27 huge-outlier profile. 498 small files (1-5 MB) plus two
    /// deterministic outliers: one 10 GB file and one 20 GB file.
    /// This is the textbook LPT case. Under input-order dispatch the
    /// two outliers land at indices 498 and 499, so 8 workers race
    /// through the small files then sit idle for many seconds waiting
    /// for the two outliers to finish. Under largest-first LPT the
    /// outliers start immediately and the other 7 workers drain the
    /// small files in parallel. If LPT does NOT win decisively here,
    /// the W2 hypothesis (production scheduling is bound by
    /// steady-state pipelining, not makespan) is refuted.
    /// </summary>
    private static long HugeOutlierSize(int index, int count, Random rng)
    {
        if (index == count - 1) return 20L * 1024 * 1024 * 1024;
        if (index == count - 2) return 10L * 1024 * 1024 * 1024;
        return rng.NextInt64(1L * 1024 * 1024, 5L * 1024 * 1024);
    }
}
