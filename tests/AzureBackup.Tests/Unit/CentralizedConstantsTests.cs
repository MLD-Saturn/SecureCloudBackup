using AzureBackup.Core;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests;

/// <summary>
/// Pins the centralized constant values so a future edit to <see cref="ByteSizes"/>
/// or <see cref="KdfParameters"/> that silently changes a byte-size unit or a
/// security work factor fails loudly here instead of in production. These guard
/// the 2026-06-02 constant-centralization refactor.
/// </summary>
public class CentralizedConstantsTests
{
    [Fact]
    public void ByteSizesKBIsOneKibibyte()
    {
        Assert.Equal(1024, ByteSizes.KB);
    }

    [Fact]
    public void ByteSizesMBIsOneMebibyte()
    {
        Assert.Equal(1_048_576, ByteSizes.MB);
    }

    [Fact]
    public void ByteSizesMBLongIsOneMebibyte()
    {
        Assert.Equal(1_048_576L, ByteSizes.MBLong);
    }

    [Fact]
    public void ByteSizesMBLongEqualsMB()
    {
        Assert.Equal(ByteSizes.MB, ByteSizes.MBLong);
    }

    [Fact]
    public void ByteSizesMBLongIsLongTyped()
    {
        Assert.IsType<long>(ByteSizes.MBLong);
    }

    [Fact]
    public void ByteSizesMBLongMultiplyDoesNotOverflowInt()
    {
        // 8192 * MB would overflow Int32 (8,589,934,592 > int.MaxValue);
        // using MBLong promotes the product to 64-bit so it computes correctly.
        const int selectedMB = 8192;

        var bytes = selectedMB * ByteSizes.MBLong;

        Assert.Equal(8_589_934_592L, bytes);
    }

    [Fact]
    public void Argon2DegreeOfParallelismIsEight()
    {
        Assert.Equal(8, KdfParameters.Argon2DegreeOfParallelism);
    }

    [Fact]
    public void Argon2MemorySizeIsSixtyFourMegabytesInKibibytes()
    {
        Assert.Equal(65_536, KdfParameters.Argon2MemorySize);
    }

    [Fact]
    public void Argon2IterationsIsThree()
    {
        Assert.Equal(3, KdfParameters.Argon2Iterations);
    }

    [Fact]
    public void SaltSizeIsSixteenBytes()
    {
        Assert.Equal(16, KdfParameters.SaltSize);
    }
}
