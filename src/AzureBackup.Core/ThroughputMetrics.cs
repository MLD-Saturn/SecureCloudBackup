using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using static AzureBackup.Core.ByteSizes;

namespace AzureBackup.Core;

/// <summary>
/// Records structured throughput metrics to a JSONL file for post-hoc analysis
/// of backup/restore pipeline performance. Each line is a self-contained JSON object.
/// <para>
/// Two record types are written:
/// <list type="bullet">
///   <item><c>file</c> — per-file metrics (chunk counts, throughput, dedup stats, pipeline stalls)</item>
///   <item><c>op</c> — operation summary (aggregate totals, elapsed time, concurrency settings)</item>
/// </list>
/// </para>
/// Thread-safe: <see cref="RecordFile"/> may be called from parallel file workers.
/// File writes are batched and serialized through a <see cref="ConcurrentQueue{T}"/>.
/// </summary>
public sealed class ThroughputMetrics : IDisposable
{
    private readonly string _metricsDirectory;
    private readonly ConcurrentQueue<MetricsRecord> _pendingRecords = new();

    // Serializes every flush so concurrent file workers can persist records
    // without racing on the shared append-only writer. The queue stays
    // lock-free for enqueue; only the disk drain is serialized.
    private readonly object _flushLock = new();

    // Long-lived writer kept open across flushes. Reopening the file per
    // flush is wasteful when restoring tens of thousands of files, so the
    // handle is created once and reused. _writerFilePath tracks which daily
    // file the handle points at so a UTC day rollover can swap it cleanly.
    private StreamWriter? _writer;
    private string? _writerFilePath;
    private bool _disposed;

    // FileStream buffer for the persistent append handle. 64 KiB amortizes
    // syscall overhead across the many small per-file JSONL rows produced by
    // a large restore without holding meaningful memory.
    private const int WriterBufferBytes = 64 * KB;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        // Apply the same naming policy to dictionary keys so the
        // DecisionMetrics.Context bag stays grep-friendly. Without this,
        // a context key like "maxParallelFileBackups" would round-trip
        // unchanged into the JSONL line, breaking the snake_case grep
        // convention used by every other field in the file.
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Maximum age of metrics files before cleanup (30 days).
    /// </summary>
    private const int RetentionDays = 30;

    /// <summary>
    /// Computes effective throughput in MEGABYTES per second from a byte
    /// count and an elapsed-seconds measurement, returning 0 when no time
    /// has elapsed (so a zero-duration sample cannot divide by zero).
    /// <para>
    /// This is the single canonical home for the <c>bytes / elapsed / MB</c>
    /// formula that populates <see cref="FileMetrics.ThroughputMBps"/> and
    /// <see cref="OperationMetrics.ThroughputMBps"/>. It was previously
    /// inlined at seven call sites across the backup and restore pipelines;
    /// the B43 factor-of-8 label bug (see the rename note on
    /// <see cref="FileMetrics.ThroughputMBps"/>) is exactly the class of
    /// error that a scattered unit-bearing formula invites, so it now lives
    /// in one place where the unit can be reasoned about once.
    /// </para>
    /// </summary>
    /// <param name="bytes">Bytes transferred during the measured window.</param>
    /// <param name="elapsedSeconds">Wall-clock duration of the window, in seconds.</param>
    /// <returns>Megabytes per second, or 0 when <paramref name="elapsedSeconds"/> is not positive.</returns>
    internal static double ComputeThroughputMBps(long bytes, double elapsedSeconds)
        => elapsedSeconds > 0 ? bytes / elapsedSeconds / MBLong : 0;

    /// <summary>
    /// Creates a new metrics logger that writes to the specified directory.
    /// The directory is created if it does not exist.
    /// </summary>
    public ThroughputMetrics(string metricsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricsDirectory);
        _metricsDirectory = metricsDirectory;

        if (!Directory.Exists(_metricsDirectory))
            Directory.CreateDirectory(_metricsDirectory);
    }

    /// <summary>
    /// Records per-file metrics from a completed backup or restore operation.
    /// Thread-safe — may be called from parallel file workers.
    /// </summary>
    public void RecordFile(FileMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        metrics.Type = "file";
        metrics.Timestamp = DateTime.UtcNow;
        _pendingRecords.Enqueue(metrics);
    }

    /// <summary>
    /// Records per-file metrics and immediately persists all pending records
    /// to disk. Use this from restore/backup file workers so each completed
    /// file becomes visible in the JSONL file as it finishes, instead of only
    /// at operation completion. Thread-safe: enqueue is lock-free and the
    /// disk drain is serialized on a shared writer, so tens of thousands of
    /// concurrent file completions persist without racing the append handle.
    /// </summary>
    public void RecordFileAndFlush(FileMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        metrics.Type = "file";
        metrics.Timestamp = DateTime.UtcNow;
        _pendingRecords.Enqueue(metrics);
        Flush();
    }

    /// <summary>
    /// Records an operation-level summary and flushes all pending records to disk.
    /// Call once at the end of each backup/restore/mirror operation.
    /// </summary>
    public void RecordOperationAndFlush(OperationMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        metrics.Type = "op";
        metrics.Timestamp = DateTime.UtcNow;
        _pendingRecords.Enqueue(metrics);
        Flush();
    }

    /// <summary>
    /// Records a system context snapshot at the start of an operation.
    /// Captures hardware and configuration so metrics can be correlated.
    /// </summary>
    public void RecordContext(string operation, int memoryBudgetMb, bool memoryBudgetEnabled)
    {
        _pendingRecords.Enqueue(new ContextMetrics
        {
            Type = "ctx",
            Timestamp = DateTime.UtcNow,
            Operation = operation,
            Processors = Environment.ProcessorCount,
            TotalRamMb = (int)(SystemMemoryHelper.GetTotalPhysicalMemoryBytes() / MBLong),
            MemoryBudgetMb = memoryBudgetMb,
            MemoryBudgetEnabled = memoryBudgetEnabled,
            Is64Bit = Environment.Is64BitProcess,
            Os = Environment.OSVersion.ToString()
        });
    }

    /// <summary>
    /// Records a corruption event with per-chunk recovery details.
    /// Immediately flushed to disk so the data survives a crash.
    /// </summary>
    public void RecordCorruption(CorruptionMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        metrics.Type = "corruption";
        metrics.Timestamp = DateTime.UtcNow;
        _pendingRecords.Enqueue(metrics);
        Flush();
    }

    /// <summary>
    /// Records a runtime decision that affects performance, with a key/value
    /// context bag. Use this to justify (in post-hoc analysis) WHY a particular
    /// concurrency / memory-budget / tier-selection / dedup short-circuit
    /// fired the way it did. Immediately flushed so the record survives a
    /// process kill that prevents the operation summary from being written.
    /// </summary>
    /// <param name="reason">Short label identifying the decision point
    /// (e.g., <c>"memory-budget-clamp"</c>, <c>"backup-concurrency"</c>).
    /// Used as a grep target across the daily JSONL file.</param>
    /// <param name="context">Optional key/value bag describing the decision
    /// inputs and outcome. Values are serialized via <see cref="object.ToString"/>.</param>
    /// <remarks>
    /// Pre-fix: when <c>MemoryBudget.FromConfig</c> clamped parallelism from
    /// 8 to 4 due to RAM constraints, the only evidence was a log line that
    /// got rotated out after 7 days. Post-hoc performance analysis could not
    /// distinguish "ran with 8 workers" from "configured for 8 but clamped
    /// to 4 mid-run" -- which is the difference between investigating an
    /// algorithm regression and a config-tuning issue.
    /// </remarks>
    public void RecordDecision(string reason, IReadOnlyDictionary<string, object?>? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var record = new DecisionMetrics
        {
            Type = "decision",
            Timestamp = DateTime.UtcNow,
            Reason = reason,
            Context = context?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "null")
        };
        _pendingRecords.Enqueue(record);
        Flush();
    }

    /// <summary>
    /// Drains all pending records to the daily JSONL file using a long-lived
    /// append writer. Serialized on <see cref="_flushLock"/> so concurrent
    /// callers cannot interleave writes or race the shared handle. Best-effort:
    /// failures never propagate to the main operation.
    /// </summary>
    private void Flush()
    {
        lock (_flushLock)
        {
            if (_disposed)
                return;

            try
            {
                var writer = GetOrCreateWriter();
                if (writer is null)
                    return;

                var wrote = false;
                while (_pendingRecords.TryDequeue(out var record))
                {
                    var json = JsonSerializer.Serialize(record, record.GetType(), JsonOptions);
                    writer.WriteLine(json);
                    wrote = true;
                }

                // Flush the StreamWriter and underlying FileStream so records
                // hit the OS file cache immediately and survive a process exit
                // that bypasses Dispose. We deliberately do not FlushToDisk()
                // every call (no fsync per file) — that would serialize 50 GB
                // restores behind disk latency for best-effort telemetry.
                if (wrote)
                    writer.Flush();
            }
            catch
            {
                // Best-effort — metrics must never break the main operation.
                // Drop the writer so the next flush rebuilds it; a transient
                // handle fault (e.g., disk full) should not wedge telemetry.
                DisposeWriter();
            }
        }
    }

    /// <summary>
    /// Returns the open append writer for today's file, creating it (or
    /// swapping it on a UTC day rollover) as needed. Must be called under
    /// <see cref="_flushLock"/>.
    /// </summary>
    private StreamWriter? GetOrCreateWriter()
    {
        var filePath = GetDailyFilePath();

        if (_writer is not null && string.Equals(_writerFilePath, filePath, StringComparison.Ordinal))
            return _writer;

        // Day rolled over (or first write): close yesterday's handle and open
        // today's. FileShare.Read lets external tooling tail the JSONL file.
        DisposeWriter();

        var stream = new FileStream(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            WriterBufferBytes);
        _writer = new StreamWriter(stream);
        _writerFilePath = filePath;
        return _writer;
    }

    /// <summary>
    /// Closes and clears the persistent writer. Must be called under
    /// <see cref="_flushLock"/>.
    /// </summary>
    private void DisposeWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // Best-effort — a failing close must not surface to callers.
        }
        finally
        {
            _writer = null;
            _writerFilePath = null;
        }
    }

    /// <summary>
    /// Deletes metrics files older than <see cref="RetentionDays"/>.
    /// Call periodically (e.g., at app startup).
    /// </summary>
    public void CleanupOldFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_metricsDirectory, "throughput-*.jsonl"))
            {
                if (File.GetCreationTimeUtc(file) < cutoff)
                {
                    FileSystemHelper.TryDelete(file);
                }
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    /// <summary>
    /// Gets the path to today's metrics file.
    /// </summary>
    private string GetDailyFilePath()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return Path.Combine(_metricsDirectory, $"throughput-{date}.jsonl");
    }

    public void Dispose()
    {
        lock (_flushLock)
        {
            if (_disposed)
                return;

            // Drain anything still queued, then close the persistent handle.
            // We inline the drain here (rather than calling Flush()) so the
            // _disposed guard can be set before releasing the lock, preventing
            // a late concurrent Flush() from reopening the writer.
            try
            {
                var writer = GetOrCreateWriter();
                if (writer is not null)
                {
                    while (_pendingRecords.TryDequeue(out var record))
                    {
                        var json = JsonSerializer.Serialize(record, record.GetType(), JsonOptions);
                        writer.WriteLine(json);
                    }
                    writer.Flush();
                }
            }
            catch
            {
                // Best-effort — disposal must never throw.
            }
            finally
            {
                DisposeWriter();
                _disposed = true;
            }
        }
    }
}

/// <summary>
/// Base class for all metrics records written to the JSONL file.
/// </summary>
[JsonDerivedType(typeof(FileMetrics))]
[JsonDerivedType(typeof(OperationMetrics))]
[JsonDerivedType(typeof(ContextMetrics))]
[JsonDerivedType(typeof(CorruptionMetrics))]
public abstract class MetricsRecord
{
    /// <summary>Record type: "file" or "op".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the record was created.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Operation kind: "backup", "restore", or "mirror".</summary>
    public string Operation { get; set; } = string.Empty;
}

/// <summary>
/// Per-file throughput metrics recorded after each file completes.
/// </summary>
public sealed class FileMetrics : MetricsRecord
{
    /// <summary>Relative or full path of the file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long Bytes { get; set; }

    /// <summary>Total number of chunks in the file.</summary>
    public int Chunks { get; set; }

    /// <summary>Smallest chunk size in bytes.</summary>
    public int ChunkMin { get; set; }

    /// <summary>Largest chunk size in bytes.</summary>
    public int ChunkMax { get; set; }

    /// <summary>Wall-clock time for this file in seconds.</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>
    /// Effective throughput for this file in MEGABYTES per second
    /// (bytes / elapsedSeconds / 1048576).
    /// <para>
    /// B43: renamed from <c>ThroughputMbps</c>. The old name advertised
    /// megabits-per-second but the producer formula at every call site
    /// divides by 1024*1024 (i.e., produces megabytes per second). The
    /// formula is correct; the label was wrong by a factor of 8 and that
    /// silently misled every downstream throughput comparison. The JSONL
    /// key is pinned via <see cref="JsonPropertyNameAttribute"/> to the
    /// fully unambiguous <c>throughput_mbytes_per_sec</c> so post-hoc
    /// grep tools cannot accidentally inherit the old wrong label.
    /// </para>
    /// </summary>
    [JsonPropertyName("throughput_mbytes_per_sec")]
    public double ThroughputMBps { get; set; }

    // ── Backup-specific fields ──

    /// <summary>Number of chunks that were new uploads (not deduplicated).</summary>
    public int NewChunks { get; set; }

    /// <summary>Number of chunks already present in Azure (deduplicated).</summary>
    public int DedupChunks { get; set; }

    /// <summary>Azure storage tier used for this file's chunks.</summary>
    public string Tier { get; set; } = string.Empty;

    // ── Restore-specific fields ──

    /// <summary>Adaptive chunk concurrency chosen for this file.</summary>
    public int EffectiveConcurrency { get; set; }

    /// <summary>Number of times MemoryBudget.AcquireAsync had to wait (stall).</summary>
    public int BudgetStalls { get; set; }

    /// <summary>Peak chunks held in the consumer's reorder buffer.</summary>
    public int ReorderMax { get; set; }

    /// <summary>Number of transient retries across all chunks in this file.</summary>
    public int Retries { get; set; }
}

/// <summary>
/// Operation-level summary metrics recorded once when the operation completes.
/// </summary>
public sealed class OperationMetrics : MetricsRecord
{
    /// <summary>Total files in the operation.</summary>
    public int Files { get; set; }

    /// <summary>Files that completed successfully.</summary>
    public int Succeeded { get; set; }

    /// <summary>Files that failed.</summary>
    public int Failed { get; set; }

    /// <summary>Total bytes transferred.</summary>
    public long Bytes { get; set; }

    /// <summary>Total chunks across all files.</summary>
    public int Chunks { get; set; }

    /// <summary>Wall-clock time for the entire operation in seconds.</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>
    /// Overall throughput in MEGABYTES per second
    /// (bytes / elapsedSeconds / 1048576). See the matching property on
    /// <see cref="FileMetrics"/> for the B43 rename rationale.
    /// </summary>
    [JsonPropertyName("throughput_mbytes_per_sec")]
    public double ThroughputMBps { get; set; }

    /// <summary>Maximum file-level parallelism used.</summary>
    public int FileConcurrency { get; set; }

    /// <summary>Memory budget configured in MB (0 = unlimited).</summary>
    public int MemoryBudgetMb { get; set; }

    /// <summary>Total transient retries across all files.</summary>
    public int Retries { get; set; }

    /// <summary>Total MemoryBudget stalls across all files.</summary>
    public int BudgetStalls { get; set; }

    /// <summary>
    /// Count of CRC validation failures observed during this operation.
    /// Incremented by <c>AzureBlobService.EncryptAndDiagnose</c> (post-encrypt
    /// CRC check) and <c>VerifyDownloadIntegrity</c> (pre-decrypt CRC check).
    /// A non-zero value here is the primary signal for the suspected
    /// CRC-in-encryption bug being investigated by the production test --
    /// see the matching <c>[CRC FAIL]</c> entries in the per-file .diag files
    /// (use <c>SessionId</c> to correlate).
    /// </summary>
    public int CrcFailCount { get; set; }

    /// <summary>
    /// Count of upload retries triggered by an integrity-check failure on
    /// the wire (MD5 mismatch detected by the BlobClient transfer pipeline).
    /// Distinguished from <see cref="Retries"/> (which counts transient
    /// network/throttling retries) so a regression in CRC behaviour shows
    /// up as a clean delta against historical runs.
    /// </summary>
    public int CrcRetryCount { get; set; }
}

/// <summary>
/// System context snapshot recorded once at the start of each operation.
/// Captures the environment so metrics can be correlated with hardware and config.
/// </summary>
public sealed class ContextMetrics : MetricsRecord
{
    /// <summary>Number of logical processors.</summary>
    public int Processors { get; set; }

    /// <summary>Total physical RAM in MB.</summary>
    public int TotalRamMb { get; set; }

    /// <summary>Memory budget configured in MB (0 = unlimited).</summary>
    public int MemoryBudgetMb { get; set; }

    /// <summary>True if memory budget is enabled.</summary>
    public bool MemoryBudgetEnabled { get; set; }

    /// <summary>True if running as a 64-bit process.</summary>
    public bool Is64Bit { get; set; }

    /// <summary>OS description.</summary>
    public string Os { get; set; } = string.Empty;
}

/// <summary>
/// Per-chunk corruption diagnostic record. Written when a file triggers
/// DataIntegrityException and enters the best-effort recovery path.
/// Machine-readable companion to the human-readable .diag files.
/// </summary>
public sealed class CorruptionMetrics : MetricsRecord
{
    /// <summary>File path that triggered corruption recovery.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Total chunks in the file.</summary>
    public int TotalChunks { get; set; }

    /// <summary>Chunks that decrypted successfully (AES-GCM tag OK).</summary>
    public int RecoveredChunks { get; set; }

    /// <summary>Chunks that were unrecoverable (AES-GCM tag mismatch or 404).</summary>
    public int UnrecoverableChunks { get; set; }

    /// <summary>Chunk indices that had CRC failures but AES-GCM success.</summary>
    public List<int> CrcFailedIndices { get; set; } = [];

    /// <summary>Chunk indices that were completely unrecoverable.</summary>
    public List<int> UnrecoverableIndices { get; set; } = [];

    /// <summary>The original DataIntegrityException message that triggered recovery.</summary>
    public string TriggerError { get; set; } = string.Empty;

    /// <summary>Path to the .diag file containing detailed per-chunk diagnostics.</summary>
    public string DiagFile { get; set; } = string.Empty;

    /// <summary>Path to the recovered file (in __corrupted__ folder).</summary>
    public string RecoveredPath { get; set; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long FileBytes { get; set; }
}

/// <summary>
/// Justification record for a runtime decision that affects performance.
/// Lets post-hoc analysis attribute throughput differences between two runs
/// to a specific configuration change instead of an algorithm change.
/// </summary>
/// <remarks>
/// Designed-in callers (commit X3): memory-budget clamps from
/// <see cref="MemoryBudget.FromConfig"/>; backup/restore concurrency
/// selection at op start. Future callers should follow the same pattern:
/// emit one decision record per choice, with the inputs that drove it
/// in <see cref="Context"/>.
/// </remarks>
public sealed class DecisionMetrics : MetricsRecord
{
    /// <summary>
    /// Short label identifying the decision point. Used as a grep target
    /// across the daily JSONL file. Examples:
    /// <c>backup-concurrency</c>, <c>restore-concurrency</c>,
    /// <c>memory-budget-clamp</c>.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Free-form key/value bag describing the inputs and outcome of the
    /// decision. Stored as <c>string</c> values so the JSONL line stays
    /// flat and greppable; non-string inputs are stringified by
    /// <see cref="ThroughputMetrics.RecordDecision"/>.
    /// </summary>
    public Dictionary<string, string>? Context { get; set; }
}
