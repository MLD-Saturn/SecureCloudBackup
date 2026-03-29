using System.Collections.Concurrent;
using System.Text;

namespace AzureBackup.Core.Services;

/// <summary>
/// Collects diagnostic entries for a single file backup or restore operation.
/// Thread-safe: multiple producer threads (parallel chunk uploads/downloads) can
/// log concurrently. On error the collected entries are flushed to a per-file
/// <c>.diag</c> log file so that all context for the failure is in one place.
/// On success the entries are discarded.
///
/// <para><b>Ambient context:</b> Lower-level services (e.g., <c>AzureBlobService</c>)
/// that don't receive a <c>FileOperationDiagnostics</c> parameter can call
/// <see cref="RecordAmbient"/> to write into the current file's diagnostics via
/// <see cref="AsyncLocal{T}"/>. The calling code sets the scope with
/// <see cref="SetAmbient"/>; it flows automatically across <c>await</c> and into
/// <c>Task.Run</c> / <c>Parallel.ForEachAsync</c>.</para>
/// </summary>
public sealed class FileOperationDiagnostics
{
    private static readonly AsyncLocal<FileOperationDiagnostics?> _ambient = new();

    /// <summary>
    /// Gets the ambient diagnostics for the current async execution context, or null.
    /// </summary>
    public static FileOperationDiagnostics? Current => _ambient.Value;

    /// <summary>
    /// Sets this instance as the ambient diagnostics for the current async flow.
    /// Returns an <see cref="IDisposable"/> that restores the previous value.
    /// </summary>
    public IDisposable SetAmbient()
    {
        var previous = _ambient.Value;
        _ambient.Value = this;
        return new AmbientScope(previous);
    }

    /// <summary>
    /// Records a message into the ambient diagnostics (if one is active).
    /// No-op when called outside an ambient scope — safe for hot paths.
    /// </summary>
    public static void RecordAmbient(string message)
    {
        _ambient.Value?.Record(message);
    }

    /// <summary>
    /// Records a chunk-level entry into the ambient diagnostics (if one is active).
    /// </summary>
    public static void RecordChunkAmbient(
        string phase,
        string chunkHash,
        int plainSize,
        int encryptedSize = 0,
        bool? crcValid = null,
        bool? md5Valid = null,
        string? extra = null)
    {
        _ambient.Value?.RecordChunkByBlob(phase, chunkHash, plainSize, encryptedSize, crcValid, md5Valid, extra);
    }

    private sealed class AmbientScope(FileOperationDiagnostics? previous) : IDisposable
    {
        public void Dispose() => _ambient.Value = previous;
    }

    private readonly ConcurrentQueue<string> _entries = new();
    private readonly string _filePath;
    private readonly string _operation;
    private readonly long _startTicks = Environment.TickCount64;
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private readonly string _diagnosticsDirectory;

    /// <summary>
    /// Creates a new per-file diagnostic collector.
    /// </summary>
    /// <param name="filePath">The local file path being backed up or restored</param>
    /// <param name="operation">Operation label (e.g., "Backup" or "Restore")</param>
    /// <param name="diagnosticsDirectory">
    /// Directory where .diag files are written.  When null, uses the system temp directory.
    /// </param>
    public FileOperationDiagnostics(string filePath, string operation, string? diagnosticsDirectory = null)
    {
        _filePath = filePath;
        _operation = operation;
        _diagnosticsDirectory = diagnosticsDirectory
            ?? Path.Combine(Path.GetTempPath(), "AzureBackup", "diagnostics");

        Record($"=== {operation} started for '{filePath}' ===");
        Record($"Machine: {Environment.MachineName}, Processors: {Environment.ProcessorCount}, OS: {Environment.OSVersion}");
        Record($"64-bit process: {Environment.Is64BitProcess}, GC.TotalMemory: {GC.GetTotalMemory(false):N0}");
    }

    /// <summary>
    /// Records a timestamped, thread-tagged diagnostic entry.
    /// Safe to call from any thread.
    /// </summary>
    public void Record(string message)
    {
        var elapsed = Environment.TickCount64 - _startTicks;
        var entry = $"[+{elapsed,7}ms] [T{Environment.CurrentManagedThreadId,3}] {message}";
        _entries.Enqueue(entry);
    }

    /// <summary>
    /// Records a chunk-level operation with standard fields for correlation.
    /// </summary>
    public void RecordChunk(
        string phase,
        int chunkIndex,
        string chunkHash,
        int plainSize,
        int encryptedSize = 0,
        bool? crcValid = null,
        bool? md5Valid = null,
        string? extra = null)
    {
        var sb = new StringBuilder(256);
        sb.Append($"[CHUNK] {phase}: idx={chunkIndex}, hash={chunkHash[..Math.Min(12, chunkHash.Length)]}..., plain={plainSize:N0}");
        if (encryptedSize > 0) sb.Append($", enc={encryptedSize:N0}");
        if (crcValid.HasValue) sb.Append($", crc={crcValid.Value}");
        if (md5Valid.HasValue) sb.Append($", md5={md5Valid.Value}");
        if (extra != null) sb.Append($", {extra}");

        Record(sb.ToString());
    }

    /// <summary>
    /// Records a chunk-level operation keyed by blob name/hash (no index available).
    /// Used by <c>AzureBlobService</c> via the ambient context.
    /// </summary>
    private void RecordChunkByBlob(
        string phase,
        string chunkHash,
        int plainSize,
        int encryptedSize = 0,
        bool? crcValid = null,
        bool? md5Valid = null,
        string? extra = null)
    {
        var sb = new StringBuilder(256);
        sb.Append($"[BLOB] {phase}: hash={chunkHash[..Math.Min(12, chunkHash.Length)]}..., plain={plainSize:N0}");
        if (encryptedSize > 0) sb.Append($", enc={encryptedSize:N0}");
        if (crcValid.HasValue) sb.Append($", crc={crcValid.Value}");
        if (md5Valid.HasValue) sb.Append($", md5={md5Valid.Value}");
        if (extra != null) sb.Append($", {extra}");

        Record(sb.ToString());
    }

    /// <summary>
    /// Records an error with exception details.
    /// </summary>
    public void RecordError(string context, Exception ex)
    {
        Record($"[ERROR] {context}: {ex.GetType().Name}: {ex.Message}");
        if (ex.StackTrace != null)
        {
            // Only first 5 frames to keep the log manageable
            var frames = ex.StackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var frame in frames.Take(5))
            {
                Record($"  {frame.Trim()}");
            }
        }

        if (ex.InnerException != null)
        {
            Record($"  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }

    /// <summary>
    /// Flushes all collected entries to a per-file .diag log file.
    /// The file name is derived from the original file name + operation + timestamp.
    /// Returns the path to the written diagnostic file, or null if writing failed.
    /// </summary>
    public string? Flush(string? errorSummary = null)
    {
        try
        {
            Directory.CreateDirectory(_diagnosticsDirectory);

            var safeFileName = MakeSafeFileName(Path.GetFileName(_filePath));
            var timestamp = _startUtc.ToString("yyyyMMdd_HHmmss");
            var diagFileName = $"{safeFileName}_{_operation}_{timestamp}.diag";
            var diagPath = Path.Combine(_diagnosticsDirectory, diagFileName);

            Record($"GC.TotalMemory at flush: {GC.GetTotalMemory(false):N0}");
            if (errorSummary != null)
            {
                Record($"[SUMMARY] {errorSummary}");
            }
            Record($"=== {_operation} diagnostics end — {_entries.Count} entries ===");

            using var writer = new StreamWriter(diagPath, append: false, Encoding.UTF8);
            foreach (var entry in _entries)
            {
                writer.WriteLine(entry);
            }

            return diagPath;
        }
        catch
        {
            return null;
        }
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        // Truncate to reasonable length
        return sb.Length > 80 ? sb.ToString(0, 80) : sb.ToString();
    }
}
