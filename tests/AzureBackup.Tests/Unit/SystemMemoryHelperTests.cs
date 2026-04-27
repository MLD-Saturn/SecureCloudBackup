using AzureBackup.Core;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Unit tests for <see cref="SystemMemoryHelper"/>.
///
/// <para>
/// Most assertions exercise the pure-function sibling
/// <see cref="SystemMemoryHelper.GetRecommendedDefaultLimitMBForTotalBytes(long)"/>
/// so the test is deterministic regardless of the host machine. The
/// existing snapshot of expected outputs (from B29's design table)
/// is encoded as <see cref="Theory"/> rows so a future change to the
/// 25% rule, the snap-to-step rule, or the
/// <see cref="SystemMemoryHelper.RecommendedDefaultLimitCapMB"/>
/// constant is caught immediately at every RAM tier rather than
/// being silently shipped.
/// </para>
/// </summary>
public class SystemMemoryHelperTests
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * MB;

    [Theory]
    [InlineData(2 * GB, 512)]      // 25% = 512 MB, fits exactly on first step.
    [InlineData(4 * GB, 1024)]     // 25% = 1 GB, fits on second step.
    [InlineData(8 * GB, 2048)]     // 25% = 2 GB, fits on third step.
    [InlineData(16 * GB, 4096)]    // 25% = 4 GB, fits on fourth step.
    [InlineData(32 * GB, 8192)]    // 25% = 8 GB, fits on the cap step.
    [InlineData(64 * GB, 8192)]    // 25% = 16 GB, capped down to 8 GB.
    [InlineData(128 * GB, 8192)]   // 25% = 32 GB, capped down to 8 GB.
    public void GetRecommendedDefaultLimitMB_AtRamTier_ReturnsExpectedSteppedValue(long totalBytes, int expectedMB)
    {
        var actual = SystemMemoryHelper.GetRecommendedDefaultLimitMBForTotalBytes(totalBytes);

        Assert.Equal(expectedMB, actual);
    }

    [Fact]
    public void GetRecommendedDefaultLimitMB_DetectionFailed_FallsBackToCap()
    {
        // Pre-B29 historical default. Used when GC.GetGCMemoryInfo
        // cannot answer (e.g. some sandboxed runtimes return 0).
        var actual = SystemMemoryHelper.GetRecommendedDefaultLimitMBForTotalBytes(0);

        Assert.Equal(SystemMemoryHelper.RecommendedDefaultLimitCapMB, actual);
    }

    [Fact]
    public void GetRecommendedDefaultLimitMB_NegativeTotalBytes_FallsBackToCap()
    {
        // Defensive: the GCMemoryInfo wrapper returns 0 on detection
        // failure, but a negative value is still a "do not trust"
        // sentinel and must not produce a nonsensical limit.
        var actual = SystemMemoryHelper.GetRecommendedDefaultLimitMBForTotalBytes(-1);

        Assert.Equal(SystemMemoryHelper.RecommendedDefaultLimitCapMB, actual);
    }

    [Fact]
    public void GetRecommendedDefaultLimitMB_TinyMachine_StillReturnsMinLimit()
    {
        // 768 MB total -> 25% = 192 MB, below MinLimitMB (512). The
        // helper clamps up to MinLimitMB so the slider always has a
        // valid stepped value to display, even on very small hosts.
        // The slider on such a host has only one step (the total),
        // and on a 768 MB host the only step is 768 MB itself.
        // GetMemorySteps(768 MB) -> [512, 768]. 25% = 192 < 512.
        // Snap-down with no-step-fits-yet branch returns MinLimitMB.
        var actual = SystemMemoryHelper.GetRecommendedDefaultLimitMBForTotalBytes(768L * MB);

        Assert.Equal(SystemMemoryHelper.MinLimitMB, actual);
    }

    [Theory]
    [InlineData(2 * GB)]
    [InlineData(4 * GB)]
    [InlineData(8 * GB)]
    [InlineData(16 * GB)]
    [InlineData(32 * GB)]
    [InlineData(64 * GB)]
    [InlineData(128 * GB)]
    public void GetRecommendedDefaultLimitMB_ResultIsAlwaysASliderDetent(long totalBytes)
    {
        // The auto-default must always be one of the stepped values
        // the UI slider can actually display. Otherwise loading the
        // config snaps the slider to a nearby step and the saved
        // budget no longer matches what the user sees on screen.
        var steps = SystemMemoryHelper.GetMemorySteps(totalBytes);
        var recommended = SystemMemoryHelper.GetRecommendedDefaultLimitMBForTotalBytes(totalBytes);

        Assert.Contains(recommended, steps);
    }

    [Theory]
    [InlineData(2 * GB)]
    [InlineData(4 * GB)]
    [InlineData(8 * GB)]
    [InlineData(16 * GB)]
    [InlineData(32 * GB)]
    [InlineData(64 * GB)]
    [InlineData(128 * GB)]
    public void GetRecommendedDefaultLimitMB_ResultIsInSafeBand(long totalBytes)
    {
        // The auto-default must land in the green Safe band on every
        // RAM tier. If this ever fails the user is opening Settings
        // and seeing a red Dangerous (or amber Aggressive) indicator
        // on a value the application chose for them.
        var recommended = SystemMemoryHelper.GetRecommendedDefaultLimitMBForTotalBytes(totalBytes);
        var severity = SystemMemoryHelper.GetSeverity(recommended, totalBytes);

        Assert.Equal(MemoryLimitSeverity.Safe, severity);
    }
}
