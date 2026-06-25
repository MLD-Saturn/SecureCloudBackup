using SecureCloudBackup.Core.Services.Backends;
using SecureCloudBackup.Crypto;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Unit tests for <see cref="CatalogFormat"/>: the non-decrypting on-disk format
/// sniffer that the <see cref="DatabaseBackendFactory"/> uses to decide whether a
/// catalog opens directly (new AZDB snapshot), needs migration (legacy SQLCipher),
/// or is a brand-new install (missing).
/// </summary>
public sealed class CatalogFormatTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public CatalogFormatTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "azbk-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "backup.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Detect_NoFile_ReturnsMissing()
    {
        var result = CatalogFormat.Detect(_dbPath);

        Assert.Equal(CatalogFormat.Kind.Missing, result);
    }

    [Fact]
    public void Detect_EmptyFile_ReturnsMissing()
    {
        File.WriteAllBytes(_dbPath, Array.Empty<byte>());

        var result = CatalogFormat.Detect(_dbPath);

        Assert.Equal(CatalogFormat.Kind.Missing, result);
    }

    [Fact]
    public void Detect_AzdbSnapshot_ReturnsSnapshot()
    {
        var snapshot = DbSnapshotEnvelope.Encrypt(new byte[] { 1, 2, 3, 4 }, "pw".AsSpan());
        File.WriteAllBytes(_dbPath, snapshot);

        var result = CatalogFormat.Detect(_dbPath);

        Assert.Equal(CatalogFormat.Kind.Snapshot, result);
    }

    [Fact]
    public void Detect_AzdbSnapshot_WithSaltSidecar_StillReturnsSnapshot()
    {
        // A leftover salt sidecar must not downgrade a real snapshot to legacy.
        var snapshot = DbSnapshotEnvelope.Encrypt(new byte[] { 9, 9, 9 }, "pw".AsSpan());
        File.WriteAllBytes(_dbPath, snapshot);
        File.WriteAllBytes(_dbPath + ".salt", new byte[16]);

        var result = CatalogFormat.Detect(_dbPath);

        Assert.Equal(CatalogFormat.Kind.Snapshot, result);
    }

    [Fact]
    public void Detect_NonAzdbFile_WithSaltSidecar_ReturnsLegacySqlCipher()
    {
        // SQLCipher databases are opaque (encrypted) and always have a salt sidecar.
        File.WriteAllBytes(_dbPath, new byte[] { 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65 });
        File.WriteAllBytes(_dbPath + ".salt", new byte[16]);

        var result = CatalogFormat.Detect(_dbPath);

        Assert.Equal(CatalogFormat.Kind.LegacySqlCipher, result);
    }

    [Fact]
    public void Detect_NonAzdbFile_WithoutSaltSidecar_ReturnsMissing()
    {
        // No salt sidecar means it is not a recognisable legacy catalog.
        File.WriteAllBytes(_dbPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var result = CatalogFormat.Detect(_dbPath);

        Assert.Equal(CatalogFormat.Kind.Missing, result);
    }

    [Fact]
    public void Detect_NullOrWhitespacePath_ReturnsMissing()
    {
        var result = CatalogFormat.Detect("   ");

        Assert.Equal(CatalogFormat.Kind.Missing, result);
    }
}
