using System.Diagnostics;

namespace SecureCloudBackup.Core;

/// <summary>
/// Computes transfer speed and estimated time remaining from cumulative byte totals.
/// Thread-safe: callers may invoke <see cref="Update"/> from any thread; the 500 ms
/// throttle ensures the output strings are recomputed at most twice per second.
/// <para>
/// Algorithm: <c>speed = totalBytesProcessed / elapsedTime</c> (cumulative average).
/// This is simple, stable, and avoids the complexity of windowed sampling.
/// </para>
/// </summary>
public sealed class SpeedTracker
{
    private readonly Stopwatch _elapsed = new();
    private long _lastUpdateTicks;

    // 500 ms throttle — avoids recalculating on every chunk
    private const long ThrottleIntervalTicks = 500 * TimeSpan.TicksPerMillisecond;

    // Minimum elapsed time before the first speed calculation (avoids division by tiny values)
    private const long MinElapsedMs = 1000;

    /// <summary>
    /// Formatted speed string (e.g. "45.7 MB/s"), or empty if not yet available.
    /// </summary>
    public string Speed { get; private set; } = string.Empty;

    /// <summary>
    /// Formatted ETA string (e.g. "2m 30s remaining"), or empty if not applicable.
    /// </summary>
    public string EstimatedTimeRemaining { get; private set; } = string.Empty;

    /// <summary>
    /// Formatted elapsed time string (e.g. "1m 15s"), or empty if not started.
    /// </summary>
    public string ElapsedText => _elapsed.IsRunning || _elapsed.ElapsedMilliseconds > 0
        ? FormatHelper.FormatDuration(_elapsed.Elapsed.TotalSeconds)
        : string.Empty;

    /// <summary>
    /// Current speed in bytes per second, or 0 if not yet available.
    /// </summary>
    public double BytesPerSecond { get; private set; }

    /// <summary>
    /// Starts or restarts the internal stopwatch and clears all computed values.
    /// Call once at the beginning of each operation.
    /// </summary>
    public void Start()
    {
        _elapsed.Restart();
        _lastUpdateTicks = 0;
        Speed = string.Empty;
        EstimatedTimeRemaining = string.Empty;
        BytesPerSecond = 0;
    }

    /// <summary>
    /// Stops the internal stopwatch. Speed and ETA are frozen at their last computed values.
    /// </summary>
    public void Stop()
    {
        _elapsed.Stop();
    }

    /// <summary>
    /// Recomputes speed and ETA from the given cumulative totals.
    /// No-ops (returns false) if the 500 ms throttle has not elapsed since the last update.
    /// </summary>
    /// <param name="totalBytesProcessed">Cumulative bytes transferred so far.</param>
    /// <param name="totalBytes">Total bytes expected for the operation (0 if unknown).</param>
    /// <returns>True if the values were recalculated; false if throttled.</returns>
    public bool Update(long totalBytesProcessed, long totalBytes)
    {
        var nowTicks = _elapsed.Elapsed.Ticks;
        if (nowTicks - _lastUpdateTicks < ThrottleIntervalTicks)
            return false;

        _lastUpdateTicks = nowTicks;

        var elapsedMs = _elapsed.ElapsedMilliseconds;
        if (elapsedMs < MinElapsedMs || totalBytesProcessed <= 0)
            return false;

        BytesPerSecond = (double)totalBytesProcessed / elapsedMs * 1000;
        Speed = $"{FormatHelper.FormatBytes((long)BytesPerSecond)}/s";

        if (BytesPerSecond > 0 && totalBytes > totalBytesProcessed)
        {
            var remainingBytes = totalBytes - totalBytesProcessed;
            var remainingSeconds = remainingBytes / BytesPerSecond;
            EstimatedTimeRemaining = $"{FormatHelper.FormatDuration(remainingSeconds)} remaining";
        }
        else
        {
            EstimatedTimeRemaining = string.Empty;
        }

        return true;
    }

    /// <summary>
    /// Clears all computed values without stopping the stopwatch.
    /// </summary>
    public void Reset()
    {
        _elapsed.Reset();
        _lastUpdateTicks = 0;
        Speed = string.Empty;
        EstimatedTimeRemaining = string.Empty;
        BytesPerSecond = 0;
    }
}
