using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Byte-level memory admission control for parallel chunk operations.
/// Each consumer acquires its actual byte cost before processing and releases when done.
/// This naturally allows more small chunks and fewer large chunks concurrently,
/// using the budget precisely rather than assuming worst-case chunk sizes for all slots.
/// <para>
/// The "at-least-one" guarantee prevents deadlock when a single chunk's cost exceeds
/// the remaining budget (e.g., a 128 MB chunk when only 90 MB remains free).
/// When nothing is in-flight, one operation is always allowed regardless of its cost.
/// </para>
/// <para>
/// Pass <see cref="long.MaxValue"/> for an unlimited budget — acquire/release become
/// no-ops, preserving the current uncapped behavior with zero overhead.
/// </para>
/// </summary>
public sealed class MemoryBudget : IDisposable
{
    private readonly long _totalBytes;
    private readonly bool _isUnlimited;
    private long _usedBytes;
    private int _waitersCount;
    private long _stallCount;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _budgetReleased = new(0, int.MaxValue);

    /// <summary>Bytes currently acquired by active operations.</summary>
    public long UsedBytes
    {
        get { lock (_lock) { return _usedBytes; } }
    }

    /// <summary>Bytes remaining in the budget.</summary>
    public long RemainingBytes => _totalBytes - UsedBytes;

    /// <summary>Total budget capacity in bytes.</summary>
    public long TotalBytes => _totalBytes;

    /// <summary>True when the budget is unlimited (no throttling).</summary>
    public bool IsUnlimited => _isUnlimited;

    /// <summary>
    /// Number of times <see cref="AcquireAsync"/> had to wait because the budget was full.
    /// Reset when <see cref="ResetStallCount"/> is called. Thread-safe.
    /// </summary>
    public long StallCount => Volatile.Read(ref _stallCount);

    /// <summary>
    /// Resets the stall counter to zero. Call at the start of each operation
    /// to get per-operation stall counts.
    /// </summary>
    public void ResetStallCount() => Interlocked.Exchange(ref _stallCount, 0);

    /// <summary>
    /// Creates a new memory budget with the specified capacity.
    /// </summary>
    /// <param name="totalBytes">
    /// Total budget in bytes. Pass <see cref="long.MaxValue"/> for unlimited (no throttling).
    /// </param>
    public MemoryBudget(long totalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        _totalBytes = totalBytes;
        _isUnlimited = totalBytes == long.MaxValue;
    }

    /// <summary>
    /// Acquires <paramref name="bytes"/> from the budget, waiting if necessary.
    /// Always allows at least one operation even if it exceeds the remaining budget,
    /// preventing deadlock when a single chunk is larger than available headroom.
    /// </summary>
    /// <param name="bytes">Cost in bytes (typically chunkSize × 2 for encrypt, × 3 for download).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AcquireAsync(long bytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        if (_isUnlimited)
            return;

        // Fast path: try to acquire without waiting
        lock (_lock)
        {
            if (_usedBytes + bytes <= _totalBytes || _usedBytes == 0)
            {
                _usedBytes += bytes;
                return;
            }
        }

        // Slow path: wait for budget to free up
        Interlocked.Increment(ref _stallCount);
        Interlocked.Increment(ref _waitersCount);
        try
        {
            while (true)
            {
                await _budgetReleased.WaitAsync(cancellationToken);

                lock (_lock)
                {
                    if (_usedBytes + bytes <= _totalBytes || _usedBytes == 0)
                    {
                        _usedBytes += bytes;
                        return;
                    }
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waitersCount);
        }
    }

    /// <summary>
    /// Returns <paramref name="bytes"/> to the budget, waking any waiting acquirers.
    /// </summary>
    public void Release(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        if (_isUnlimited)
            return;

        lock (_lock)
        {
            _usedBytes = Math.Max(0, _usedBytes - bytes);
        }

        // Wake all waiting acquirers so they can re-check whether their request fits.
        var waiters = Volatile.Read(ref _waitersCount);
        if (waiters > 0)
        {
            try { _budgetReleased.Release(waiters); }
            catch (SemaphoreFullException) { /* already signaled */ }
        }
    }

    /// <summary>
    /// Creates a budget from the user's configuration.
    /// Returns an unlimited budget when memory limiting is disabled.
    /// Reserves <paramref name="fixedOverheadBytes"/> for non-chunk allocations
    /// (CDC buffer, file streams, etc.) so the budget reflects only the
    /// memory available for in-flight chunks.
    /// </summary>
    /// <param name="config">Backup configuration with memory limit settings.</param>
    /// <param name="fixedOverheadBytes">
    /// Bytes reserved for fixed allocations that are always present during the operation.
    /// </param>
    public static MemoryBudget FromConfig(BackupConfiguration config, long fixedOverheadBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegative(fixedOverheadBytes);

        if (!config.MemoryLimitEnabled)
            return new MemoryBudget(long.MaxValue);

        var totalBytes = (long)config.MemoryLimitMB * 1024 * 1024;
        var available = Math.Max(totalBytes - fixedOverheadBytes, 1);
        return new MemoryBudget(available);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _budgetReleased.Dispose();
    }
}
