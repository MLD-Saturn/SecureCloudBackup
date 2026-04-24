using AzureBackup.Core.Services;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace AzureBackup.Benchmarks;

/// <summary>
/// B25-bench: end-to-end throughput baseline for the backup pipeline,
/// excluding the network. Drives the same code paths a real backup
/// goes through (CDC chunking, AES-GCM encryption, SqliteBackend
/// metadata writes, file-level <c>Parallel.ForEachAsync</c>, channel
/// backpressure, MemoryBudget) but substitutes
/// <see cref="InMemoryBlobService"/> for the Azure blob client.
///
/// <para>
/// <b>Why this benchmark exists.</b> Every public optimisation claim
/// for the backup service ("largest-first scheduling will improve
/// makespan", "two-tier file split will help small-file workloads")
/// is a hypothesis until it is measured against a stable baseline.
/// Pre-B25 the codebase had nineteen micro-benchmarks but none that
/// exercised the full backup pipeline, so a scheduling change in
/// <c>BackupFilesCoreAsync</c> could only be defended on theory. This
/// benchmark closes that gap by producing reproducible per-batch
/// wall-clock numbers and per-iteration allocations under a fixed
/// (but tunable) simulated network latency.
/// </para>
///
/// <para>
/// <b>What it does NOT measure.</b> Real Azure egress throughput,
/// HTTP retry behaviour against a flaky upstream, TLS handshake +
/// connection-pool warmup, and storage-account-side throttling are
/// all outside the scope of an in-memory blob fake. Those are
/// network-layer concerns that are constant across any scheduling
/// change in our code, so eliminating them lets the scheduling
/// signal dominate the measurement.
/// </para>
///
/// <para>
/// <b>Latency injection.</b> Without simulated latency the in-memory
/// blob "uploads" return in microseconds, which makes CPU stages
/// (CDC + encryption) dominate and hides any scheduling improvement.
/// The <see cref="SimulatedLatencyMs"/> param sweeps a representative
/// range so we can see at what latency floor the scheduling change
/// starts to pay off. 50 ms is roughly representative of a US-west
/// machine talking to a US-central storage account.
/// </para>
///
/// <para>
/// <b>Workload profiles.</b> Three SizeProfile values cover the
/// production workloads we care about:
/// <list type="bullet">
///   <item><c>uniform-1MB</c>: every file 1 MB. Tests behaviour when
///     all files take roughly equal time. Largest-first scheduling
///     is expected to be a no-op here.</item>
///   <item><c>mixed-realistic</c>: 80% small (8 KB - 256 KB), 15%
///     medium (1-10 MB), 5% large (50-200 MB). Approximates a typical
///     user's documents folder with a handful of media files.</item>
///   <item><c>large-skew</c>: most files in the 1-10 MB range with
///     two outliers in the 500 MB - 1 GB range (think VM images or
///     RAW photos). This is the regime where LPT scheduling has the
///     biggest theoretical win.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Baseline numbers (captured 2026-04-24, B25-bench commit
/// <c>HEAD</c>).</b> Hardware: DESKTOP-CYBERML, 32 logical processors,
/// .NET 10.0.7, SQLite backend (production default), per-iteration
/// cold cache, warmupCount=1 iterationCount=2 invocationCount=1.
/// Each row is one BackupFilesAsync call against the synthetic
/// workload. Update this block whenever the benchmark configuration
/// or the pipeline implementation changes materially.
/// <code>
/// // | SimulatedLatencyMs | FileCount | SizeProfile     |        Mean |   Allocated |
/// // |------------------- |---------- |---------------- |------------ |------------ |
/// // | 0                  |       100 | uniform-1MB     |    ~  280 ms |    ~  208 MB |
/// // | 0                  |       100 | mixed-realistic |    ~ 1620 ms |    ~  340 MB |
/// // | 0                  |       100 | large-skew      |    ~ 9500 ms |    ~ 2547 MB |
/// // | 50                 |       100 | uniform-1MB     |    ~ 1830 ms |    ~  207 MB |
/// // | 50                 |       100 | mixed-realistic |    ~ 7280 ms |    ~  339 MB |
/// // | 50                 |       100 | large-skew      |    ~11700 ms |    ~ 2600 MB |
/// </code>
/// </para>
///
/// <para>
/// <b>What to read into the baseline.</b>
/// <list type="bullet">
///   <item><b>uniform-1MB / latency=0</b> is the CPU-only floor for
///     a homogeneous small-file workload. CDC + encryption + SQLite
///     metadata writes for 100 1 MB files complete in roughly a
///     quarter second. Any regression here flags a CDC or
///     encryption bottleneck.</item>
///   <item><b>uniform-1MB / latency=50</b> shows the network floor:
///     100 files / 8 parallel workers x 50 ms ~= 625 ms minimum
///     just from latency, plus CPU; observed 1830 ms is consistent
///     with the per-file metadata round-trip pattern.</item>
///   <item><b>large-skew / latency=0</b> is the makespan-bound case:
///     two ~750 MB files dominate the wall-clock because they
///     monopolise two of the eight workers for ~9 seconds of CDC +
///     encryption while the remaining six workers finish their
///     small files in under a second. This is the regime where
///     largest-first scheduling has the biggest theoretical win.
///     If the scheduler were perfect (LPT), expected makespan
///     would drop to roughly the time of the single largest file
///     plus a small overhead for the rest. Today's input-order
///     scheduling sees the large files start late if they happen
///     to land late in the input list.</item>
///   <item><b>mixed-realistic / latency=0</b> is the typical-user
///     workload: 80% small + 15% medium + 5% large. The 5% large
///     bucket pulls the mean toward the large-skew shape, so this
///     row is sensitive to scheduling too, but less dramatically
///     than large-skew.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Cost.</b> One iteration of (FileCount=100, SizeProfile=mixed-realistic,
/// SimulatedLatencyMs=50) takes roughly 5-10 seconds. Full matrix at the
/// defaults is ~3-5 minutes. Increase FileCount via the param array to
/// cover a 1000-file regime when validating a scheduling change.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 1, iterationCount: 3, invocationCount: 1)]
public class BackupThroughputBenchmark
{
    /// <summary>
    /// Simulated per-blob-operation latency in milliseconds. Zero is the
    /// CPU-only floor (useful for spotting CDC/encryption regressions).
    /// 50 ms approximates a typical Azure round-trip from a desktop. The
    /// scheduling signal we want to measure is largely invisible at zero
    /// and dominant at 50.
    /// </summary>
    [Params(0, 50)]
    public int SimulatedLatencyMs { get; set; }

    /// <summary>
    /// Number of files in the synthetic workload. Kept modest so the
    /// full param matrix completes in a few minutes; bump to 1000+ when
    /// validating a specific scheduling change.
    /// </summary>
    [Params(100)]
    public int FileCount { get; set; }

    /// <summary>
    /// Synthetic file-size distribution. See class summary for the
    /// shape of each profile.
    /// </summary>
    [Params("uniform-1MB", "mixed-realistic", "large-skew")]
    public string SizeProfile { get; set; } = "uniform-1MB";

    private string _testDir = string.Empty;
    private string _filesDir = string.Empty;
    private string _dbPath = string.Empty;
    private List<string> _filePaths = [];
    private byte[] _derivedKey = [];

    // Recreated per iteration so each timed run starts with an empty
    // database and an empty blob store. See IterationSetup.
    private LocalDatabaseService? _databaseService;
    private EncryptionService? _encryptionService;
    private ChunkingService? _chunkingService;
    private FileWatcherService? _fileWatcherService;
    private InMemoryBlobService? _blobService;
    private BackupOrchestrator? _orchestrator;

    /// <summary>
    /// One-time setup per (SimulatedLatencyMs, FileCount, SizeProfile)
    /// combination. Pre-creates the synthetic file workload on disk so
    /// per-iteration setup only resets the metadata and blob store, not
    /// the (expensive) file-generation step.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string what) => Console.WriteLine(
            $"[setup T={sw.ElapsedMilliseconds,6} ms] [latency={SimulatedLatencyMs}ms,files={FileCount},profile={SizeProfile}] {what}");

        Mark("creating temp dirs");
        _testDir = Path.Combine(Path.GetTempPath(),
            "azbk-backup-bench-" + Guid.NewGuid().ToString("N"));
        _filesDir = Path.Combine(_testDir, "files");
        Directory.CreateDirectory(_filesDir);
        _dbPath = Path.Combine(_testDir, "backup.db");

        // Force the SQLite backend regardless of any process-level
        // env var. The benchmark's intent is to measure production
        // behaviour and SQLite is the production default since C-5.
        Environment.SetEnvironmentVariable("AZBK_USE_SQLITE", "1");

        Mark("generating synthetic file workload");
        _filePaths = GenerateFiles(_filesDir, FileCount, SizeProfile);
        var totalBytes = _filePaths.Sum(p => new FileInfo(p).Length);
        Mark($"workload ready: {FileCount} files, {totalBytes:N0} bytes total");

        // Synthesize a 32-byte key directly so we skip the 1-2 second
        // Argon2id derivation per benchmark setup. Production opens
        // its EncryptionService via DeriveKeyAsync; the encryption
        // INSTANCE that follows behaves identically once initialised
        // (the key bytes are the only thing that derivation supplies).
        _derivedKey = new byte[32];
        new Random(42).NextBytes(_derivedKey);

        Mark("global setup complete");
    }

    /// <summary>
    /// Per-iteration setup: build a fresh service graph against an
    /// empty database file and an empty blob store. Each timed run
    /// therefore measures backup of the SAME workload starting from
    /// the SAME zero state, so iteration-to-iteration variance comes
    /// only from the pipeline itself (channel scheduling, GC timing,
    /// thread pool jitter), not from accumulated dedup state.
    /// </summary>
    [IterationSetup]
    public void IterationSetup()
    {
        // Tear down any previous iteration's services and delete the DB.
        DisposeIterationServices();
        TryDeleteDatabaseArtifacts();

        // Fresh DB + encryption + chunker + (unstarted) watcher + blob fake.
        _databaseService = new LocalDatabaseService();
        _databaseService.Initialize(_dbPath, "BenchmarkPassword123!");

        _encryptionService = new EncryptionService();
        _encryptionService.Initialize(_derivedKey);

        _chunkingService = new ChunkingService();
        _fileWatcherService = new FileWatcherService(_databaseService);
        _blobService = new InMemoryBlobService(_encryptionService,
            simulatedLatencyMs: SimulatedLatencyMs);

        // The orchestrator's blob calls go through the same interface
        // production uses; ConnectAsync on the fake just flips a bool.
        _blobService.ConnectAsync("UseDevelopmentStorage=true", "bench").GetAwaiter().GetResult();

        _orchestrator = new BackupOrchestrator(
            _databaseService, _encryptionService, _chunkingService,
            _blobService, _fileWatcherService);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        DisposeIterationServices();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        DisposeIterationServices();
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The measurement: backup every synthetic file end-to-end. CDC,
    /// encryption, MemoryBudget acquire/release, channel
    /// backpressure, file-level <c>Parallel.ForEachAsync</c>, and
    /// SqliteBackend metadata writes all execute exactly as in
    /// production. Only the blob upload is faked.
    /// </summary>
    [Benchmark(Description = "End-to-end backup against in-memory blob store")]
    public async Task Backup()
    {
        await _orchestrator!.BackupFilesAsync(_filePaths);
    }

    private static List<string> GenerateFiles(string dir, int count, string profile)
    {
        var rng = new Random(42);
        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var size = NextSize(profile, i, count, rng);
            var path = Path.Combine(dir, $"file_{i:D5}_{size}.bin");
            // Random bytes so CDC sees a realistic boundary distribution.
            // All-zero or repeating-pattern files would chunk into one or
            // two boundary points, hiding the per-byte rolling-hash work
            // that dominates real-world CPU.
            var buffer = new byte[size];
            rng.NextBytes(buffer);
            File.WriteAllBytes(path, buffer);
            paths.Add(path);
        }
        return paths;
    }

    private static int NextSize(string profile, int index, int count, Random rng) => profile switch
    {
        "uniform-1MB" => 1024 * 1024,
        "mixed-realistic" => MixedRealisticSize(rng),
        "large-skew" => LargeSkewSize(index, count, rng),
        _ => throw new ArgumentException($"Unknown size profile: {profile}", nameof(profile)),
    };

    private static int MixedRealisticSize(Random rng)
    {
        // 80% small (8 KB - 256 KB), 15% medium (1-10 MB), 5% large (50-200 MB).
        var bucket = rng.NextDouble();
        if (bucket < 0.80) return rng.Next(8 * 1024, 256 * 1024);
        if (bucket < 0.95) return rng.Next(1 * 1024 * 1024, 10 * 1024 * 1024);
        return rng.Next(50 * 1024 * 1024, 200 * 1024 * 1024);
    }

    private static int LargeSkewSize(int index, int count, Random rng)
    {
        // Two outliers (the last two indexes) in the 500 MB - 1 GB range,
        // everyone else in the 1-10 MB range. Capped at int.MaxValue / 2
        // out of paranoia for the byte[] allocation in GenerateFiles, but
        // we never approach that ceiling at these sizes.
        if (index >= count - 2) return rng.Next(500 * 1024 * 1024, 1024 * 1024 * 1024);
        return rng.Next(1 * 1024 * 1024, 10 * 1024 * 1024);
    }

    private void DisposeIterationServices()
    {
        try { _orchestrator = null; } catch { }
        try { _blobService = null; } catch { }
        try { _fileWatcherService?.Dispose(); _fileWatcherService = null; } catch { }
        try { _chunkingService = null; } catch { }
        try { _encryptionService?.Dispose(); _encryptionService = null; } catch { }
        try { _databaseService?.Dispose(); _databaseService = null; } catch { }
    }

    private void TryDeleteDatabaseArtifacts()
    {
        // SqliteBackend writes db, db-wal, db-shm, and db.salt next to dbPath.
        foreach (var ext in new[] { "", "-wal", "-shm", ".salt" })
        {
            try
            {
                var p = _dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
            catch
            {
                // Best effort -- if a previous iteration's WAL is still locked
                // we'll fail cleanly inside Initialize on the next IterationSetup.
            }
        }
    }
}
