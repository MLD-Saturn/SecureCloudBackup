using AzureBackup.Core;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for <see cref="SpeedTracker"/>.
/// </summary>
public class SpeedTrackerTests
{
    [Fact]
    public void WhenNotStartedThenSpeedIsEmpty()
    {
        var tracker = new SpeedTracker();

        Assert.Equal(string.Empty, tracker.Speed);
        Assert.Equal(string.Empty, tracker.EstimatedTimeRemaining);
        Assert.Equal(string.Empty, tracker.ElapsedText);
        Assert.Equal(0, tracker.BytesPerSecond);
    }

    [Fact]
    public void WhenStartedWithZeroBytesProcessedThenUpdateReturnsFalse()
    {
        var tracker = new SpeedTracker();
        tracker.Start();

        // Even after start, 0 bytes processed should not produce speed
        var updated = tracker.Update(0, 1000);

        Assert.False(updated);
        Assert.Equal(string.Empty, tracker.Speed);
    }

    [Fact]
    public async Task WhenUpdatedAfterElapsedTimeThenSpeedIsCalculated()
    {
        var tracker = new SpeedTracker();
        tracker.Start();

        // Wait beyond the 1-second minimum elapsed threshold
        await Task.Delay(1100);

        var updated = tracker.Update(10_000_000, 100_000_000);

        Assert.True(updated);
        Assert.NotEqual(string.Empty, tracker.Speed);
        Assert.EndsWith("/s", tracker.Speed);
        Assert.True(tracker.BytesPerSecond > 0);
    }

    [Fact]
    public async Task WhenBytesRemainingThenEstimatedTimeRemainingIsPopulated()
    {
        var tracker = new SpeedTracker();
        tracker.Start();

        await Task.Delay(1100);

        tracker.Update(50_000_000, 100_000_000);

        Assert.NotEqual(string.Empty, tracker.EstimatedTimeRemaining);
        Assert.Contains("remaining", tracker.EstimatedTimeRemaining);
    }

    [Fact]
    public async Task WhenAllBytesProcessedThenEstimatedTimeRemainingIsEmpty()
    {
        var tracker = new SpeedTracker();
        tracker.Start();

        await Task.Delay(1100);

        tracker.Update(100_000_000, 100_000_000);

        Assert.Equal(string.Empty, tracker.EstimatedTimeRemaining);
    }

    [Fact]
    public async Task WhenThrottledThenUpdateReturnsFalse()
    {
        var tracker = new SpeedTracker();
        tracker.Start();

        await Task.Delay(1100);

        // First call should succeed
        var first = tracker.Update(10_000_000, 100_000_000);
        Assert.True(first);

        // Immediate second call should be throttled
        var second = tracker.Update(20_000_000, 100_000_000);
        Assert.False(second);
    }

    [Fact]
    public async Task WhenStoppedThenElapsedTextIsFrozen()
    {
        var tracker = new SpeedTracker();
        tracker.Start();

        await Task.Delay(1100);
        tracker.Stop();

        var elapsed = tracker.ElapsedText;
        Assert.NotEqual(string.Empty, elapsed);

        // After stop, elapsed text should not change
        await Task.Delay(100);
        Assert.Equal(elapsed, tracker.ElapsedText);
    }

    [Fact]
    public void WhenResetThenAllValuesAreCleared()
    {
        var tracker = new SpeedTracker();
        tracker.Start();
        tracker.Stop();

        tracker.Reset();

        Assert.Equal(string.Empty, tracker.Speed);
        Assert.Equal(string.Empty, tracker.EstimatedTimeRemaining);
        Assert.Equal(string.Empty, tracker.ElapsedText);
        Assert.Equal(0, tracker.BytesPerSecond);
    }

    [Fact]
    public async Task WhenStartCalledAgainThenPreviousStateIsCleared()
    {
        var tracker = new SpeedTracker();
        tracker.Start();

        await Task.Delay(1100);
        tracker.Update(50_000_000, 100_000_000);

        Assert.NotEqual(string.Empty, tracker.Speed);

        // Restart should clear everything
        tracker.Start();

        Assert.Equal(string.Empty, tracker.Speed);
        Assert.Equal(string.Empty, tracker.EstimatedTimeRemaining);
        Assert.Equal(0, tracker.BytesPerSecond);
    }

    [Fact]
    public async Task WhenElapsedTextQueriedDuringOperationThenReturnsFormattedDuration()
    {
        var tracker = new SpeedTracker();
        tracker.Start();

        await Task.Delay(1100);

        var text = tracker.ElapsedText;
        Assert.NotEqual(string.Empty, text);
        // Should be "1s" or "2s" approximately
        Assert.Matches(@"^\d+s$", text);
    }
}
