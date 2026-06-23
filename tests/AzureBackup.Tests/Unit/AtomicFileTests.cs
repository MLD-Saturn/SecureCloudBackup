using AzureBackup.Crypto;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// Tests for <see cref="AtomicFile"/>, the shared write-temp-then-rename helper
/// used by the snapshot backend and the migration helper.
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir;

    public AtomicFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "azbk-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void WriteAllBytesAtomic_WritesExactBytes()
    {
        var path = Path.Combine(_dir, "a.bin");
        var data = new byte[] { 1, 2, 3, 4, 5 };

        AtomicFile.WriteAllBytesAtomic(path, data);

        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteAllBytesAtomic_OverwritesExistingFile()
    {
        var path = Path.Combine(_dir, "b.bin");
        File.WriteAllBytes(path, new byte[] { 9, 9, 9 });

        AtomicFile.WriteAllBytesAtomic(path, new byte[] { 7, 7 });

        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteAllBytesAtomic_CreatesMissingDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deep", "c.bin");

        AtomicFile.WriteAllBytesAtomic(path, new byte[] { 42 });

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WriteAllBytesAtomic_LeavesNoTempResidue()
    {
        var path = Path.Combine(_dir, "d.bin");

        AtomicFile.WriteAllBytesAtomic(path, new byte[] { 1 });
        AtomicFile.WriteAllBytesAtomic(path, new byte[] { 2 });

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    [Fact]
    public void WriteAllBytesAtomic_WritesEmptyArray()
    {
        var path = Path.Combine(_dir, "e.bin");

        AtomicFile.WriteAllBytesAtomic(path, []);

        Assert.True(File.Exists(path));
        Assert.Empty(File.ReadAllBytes(path));
    }
}
