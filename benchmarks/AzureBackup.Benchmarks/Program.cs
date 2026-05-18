using BenchmarkDotNet.Running;

namespace AzureBackup.Benchmarks;

/// <summary>
/// Entry point for local-developer benchmarks. Not run in CI.
///
/// Usage:
///     dotnet run -c Release --project benchmarks/AzureBackup.Benchmarks
///
/// Or to filter to a single benchmark:
///     dotnet run -c Release --project benchmarks/AzureBackup.Benchmarks -- --filter *Phase1*
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        // W5 Phase 1 (B64): fail fast if the BackupMemoryReporter
        // emit format has drifted from MemoryFidelityCollector's
        // regexes. Without this check a future format change would
        // cause every fidelity column to render as "-" silently;
        // the user only finds out hours into a Phase 2 baseline run.
        VerifyMemoryFidelityParserContract();

        // W5 Phase 1.1 (B64): publish a per-run shared directory the
        // child process can append per-iteration JSON-lines fidelity
        // samples into. Without this seam the host-side column
        // provider sees an empty singleton (BDN's default toolchain
        // forks a child per benchmark method) and every fidelity
        // column renders as "-". The directory name is randomised so
        // concurrent benchmark invocations cannot stomp each other,
        // and it lives under the system temp dir so OS cleanup
        // eventually reclaims abandoned runs. Honour an existing
        // value if one is already set (e.g. a CI runner that wants
        // a stable location).
        var existingDir = Environment.GetEnvironmentVariable(
            MemoryFidelityCollector.SamplesDirEnvVar);
        if (string.IsNullOrWhiteSpace(existingDir))
        {
            var dir = Path.Combine(
                Path.GetTempPath(),
                "azbk-bench-fidelity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable(
                MemoryFidelityCollector.SamplesDirEnvVar, dir);
            Console.WriteLine(
                $"[B64] Memory-fidelity samples will be persisted to: {dir}");
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    private static void VerifyMemoryFidelityParserContract()
    {
        // Synthetic line shaped exactly like
        // BackupMemoryReporter.EmitSample emits with a wired
        // LargeChunkBufferPool. The numbers here are arbitrary but
        // distinct so a regex misalignment would surface as a
        // wrong-value mismatch rather than a silent zero.
        const string sample =
            "[mem] backup t+12s | budget used=4096 MB / 8192 MB (50.0%, peak=6144 MB) | " +
            "stalls +0 (total 7) | oversized +0 (total 3) | " +
            "gcHeap=512 MB | gcLoad=8192 MB | " +
            "workingSet=10240 MB | privateMem=10240 MB | " +
            "unaccounted=2048 MB | gcCollections=[3,1,0] | " +
            "lohPool=512 MB cached (peak=768 MB, dropped=0, hit=80%)";

        MemoryFidelityCollector.Instance.StartIteration("__contract_check__", "__sample__", 8192);
        MemoryFidelityCollector.Instance.RecordSampleLine(sample);
        MemoryFidelityCollector.Instance.EndIteration(bytesProcessed: 0);

        var result = MemoryFidelityCollector.Instance.Results
            .FirstOrDefault(r => r.BenchmarkName == "__contract_check__");

        if (result is null) throw new InvalidOperationException(
            "MemoryFidelityCollector contract check failed: no result recorded.");

        const long MB = 1024L * 1024L;
        if (result.PeakWorkingSetBytes != 10240 * MB) throw Drift("workingSet", 10240, result.PeakWorkingSetBytes / MB);
        if (result.MaxUnaccountedBytes != 2048 * MB) throw Drift("unaccounted", 2048, result.MaxUnaccountedBytes / MB);
        if (result.PeakBudgetUsedBytes != 6144 * MB) throw Drift("peak budget", 6144, result.PeakBudgetUsedBytes / MB);
        if (result.StallCount != 7) throw Drift("stalls", 7, result.StallCount);
        if (result.OversizedAdmissions != 3) throw Drift("oversized", 3, result.OversizedAdmissions);

        VerifyMemoryFidelityPersistenceRoundTrip();

        static InvalidOperationException Drift(string field, long expected, long actual) =>
            new($"MemoryFidelityCollector parser drift on '{field}': expected {expected}, got {actual}. " +
                "BackupMemoryReporter.EmitSample format has changed; update MemoryFidelityCollector regexes.");
    }

    private static void VerifyMemoryFidelityPersistenceRoundTrip()
    {
        // W5 Phase 1.1 (B64): the host process is about to publish an
        // env var that the child process will consume by appending
        // per-iteration JSON-lines records. If serialisation or the
        // env-var-driven path is ever broken, every fidelity column
        // silently regresses to "-" again. Round-trip a synthetic
        // record through an isolated temp dir to fail fast instead.
        var dir = Path.Combine(
            Path.GetTempPath(),
            "azbk-bench-fidelity-selftest-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable(
            MemoryFidelityCollector.SamplesDirEnvVar);
        try
        {
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable(
                MemoryFidelityCollector.SamplesDirEnvVar, dir);

            MemoryFidelityCollector.Instance.StartIteration("__persist_check__", "__sample__", 4096);
            MemoryFidelityCollector.Instance.RecordSampleLine(
                "[mem] backup t+1s | budget used=1024 MB / 4096 MB (25.0%, peak=1024 MB) | " +
                "stalls +0 (total 0) | oversized +0 (total 0) | " +
                "gcHeap=128 MB | gcLoad=1024 MB | " +
                "workingSet=2048 MB | privateMem=2048 MB | " +
                "unaccounted=896 MB | gcCollections=[0,0,0]");
            MemoryFidelityCollector.Instance.EndIteration(bytesProcessed: 1024L * 1024 * 1024);

            var loaded = MemoryFidelityCollector.LoadPersistedResults();
            var match = loaded.FirstOrDefault(r => r.BenchmarkName == "__persist_check__");
            if (match is null) throw new InvalidOperationException(
                "Memory-fidelity persistence round-trip failed: nothing was loaded back from " + dir);
            const long MB = 1024L * 1024L;
            if (match.PeakWorkingSetBytes != 2048 * MB) throw new InvalidOperationException(
                $"Memory-fidelity persistence round-trip drift on workingSet: expected 2048 MB, got {match.PeakWorkingSetBytes / MB} MB.");
            if (match.BytesProcessed != 1024L * 1024 * 1024) throw new InvalidOperationException(
                $"Memory-fidelity persistence round-trip drift on bytesProcessed: expected 1 GB, got {match.BytesProcessed} bytes.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                MemoryFidelityCollector.SamplesDirEnvVar, previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
