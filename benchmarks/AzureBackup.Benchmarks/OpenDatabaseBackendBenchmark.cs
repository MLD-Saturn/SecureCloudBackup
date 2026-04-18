using AzureBackup.Core.Services.Backends;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Option C / C-3 (6/N): head-to-head comparison of database
/// open + decrypt cost. The realistic production scenario this models
/// is "user launches the app; backend opens an existing encrypted DB
/// and becomes ready to serve queries". Measured per-launch latency
/// determines whether we need a "Decrypting database..." splash
/// screen.
///
/// <para>
/// <b>Expected outcome.</b> SQLite is structurally expected to LOSE
/// here. Argon2id with the configured parameters takes ~100-300 ms
/// per call regardless of DB size; LiteDB's open path does no KDF
/// work. The eval doc \u00a79 ship gate has already been cleared by C-3
/// (5b) so this benchmark is for completeness and UX-cost sizing,
/// not for ship/no-ship.
/// </para>
///
/// <para>
/// <b>Per-iteration semantics.</b> GlobalSetup creates the DB once
/// (paying the schema-create cost ONCE so it does not bias the
/// measurement). Each [Benchmark] iteration:
/// </para>
/// <list type="number">
///   <item>Constructs a fresh backend instance.</item>
///   <item>Calls Initialize with the same password as GlobalSetup
///     (hits the SAME salt file, so the same derived key).</item>
///   <item>Disposes the backend.</item>
/// </list>
///
/// <para>
/// The measured value is wall-clock from constructor to "ready to
/// query". Disposal is included because users will close + reopen the
/// app on the same DB; the cumulative cost matters for the second-open
/// case too.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 2, iterationCount: 5, invocationCount: 1)]
public class OpenDatabaseBackendBenchmark
{
    private const string Password = "BenchmarkPassword123!";

    [Params("LiteDB", "SQLite")]
    public string Backend { get; set; } = "LiteDB";

    private string _testDir = string.Empty;
    private string _dbPath = string.Empty;

    /// <summary>
    /// Creates the temp directory and the encrypted DB ONCE per
    /// (Backend) parameter. Subsequent [Benchmark] iterations open
    /// THIS database, so every measurement hits the existing-DB path.
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string what) => Console.WriteLine(
            $"[setup T={sw.ElapsedMilliseconds,6} ms] [backend={Backend}] {what}");

        Mark("creating temp dir");
        _testDir = Path.Combine(Path.GetTempPath(),
            "azbk-open-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "bench.db");

        // Create the DB once. The schema-creation work and (for SQLite)
        // the salt-file generation happen here, NOT inside the benchmark
        // loop. Subsequent Initialize calls skip schema creation
        // (CREATE TABLE IF NOT EXISTS is a no-op) and reuse the salt.
        Mark("creating + populating DB");
        IDatabaseBackend seedBackend = Backend switch
        {
            "LiteDB" => new LiteDbBackend(),
            "SQLite" => new SqliteBackend(),
            _ => throw new InvalidOperationException($"Unknown backend: {Backend}"),
        };
        seedBackend.Initialize(_dbPath, Password.AsSpan());

        // Seed a small amount of data so the open path has SOMETHING to
        // verify against. Helps the SQLite open path actually decrypt
        // page 1 (the wrong-password probe). Production DBs are never
        // empty so this is more realistic than a totally bare schema.
        seedBackend.SetIndexMetadata("warmup", DateTime.UtcNow);
        seedBackend.Dispose();

        Mark($"setup complete (total {sw.ElapsedMilliseconds} ms)");
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// One full open cycle: construct + Initialize + Dispose. The
    /// measurement is the total wall-clock of going from "no backend"
    /// to "queryable backend" and back to "disposed".
    /// </summary>
    [Benchmark]
    public void OpenAndDispose()
    {
        IDatabaseBackend backend = Backend switch
        {
            "LiteDB" => new LiteDbBackend(),
            "SQLite" => new SqliteBackend(),
            _ => throw new InvalidOperationException($"Unknown backend: {Backend}"),
        };
        backend.Initialize(_dbPath, Password.AsSpan());
        backend.Dispose();
    }
}
