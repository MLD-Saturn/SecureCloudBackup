using System.Buffers;
using System.Security.Cryptography;
using SecureCloudBackup.Core;
using SecureCloudBackup.Core.Models;
using SecureCloudBackup.Core.Services;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Provider-agnostic behavioral contract for <see cref="IBlobStorageService"/>.
/// Every storage backend -- the in-memory test double today, a real cloud
/// provider in the future -- must satisfy these assertions.
/// <para>
/// A concrete derived class supplies a CONNECTED service via
/// <see cref="CreateConnectedServiceAsync"/>; the test bodies here touch ONLY
/// the <see cref="IBlobStorageService"/> surface (never an implementation-
/// specific member such as <c>InMemoryBlobService.StoredBlobNames</c>) so they
/// transfer unchanged to any implementation. This abstract class carries the
/// [Fact] methods and xUnit runs them once per concrete subclass; xUnit does
/// not discover tests on abstract classes directly.
/// </para>
/// <para>
/// This is the regression net the object-storage provider-abstraction refactor
/// (and any future provider) is verified against: when a new provider is added,
/// derive a sibling subclass that connects it and the entire contract runs as-is.
/// </para>
/// </summary>
public abstract class ObjectStorageContractTests : IAsyncLifetime
{
    private const string TestPassword = "ObjectStorageContractPassword123!";

    /// <summary>Encryption service the subject is bound to. Owned by the harness.</summary>
    protected EncryptionService Encryption { get; private set; } = null!;

    /// <summary>The connected service under test, exposed only through its interface.</summary>
    protected IBlobStorageService Service { get; private set; } = null!;

    /// <summary>
    /// Creates a CONNECTED storage service bound to <paramref name="encryption"/>.
    /// Implementations connect to whatever backing store they represent so the
    /// contract bodies can assume a ready-to-use service.
    /// </summary>
    protected abstract Task<IBlobStorageService> CreateConnectedServiceAsync(EncryptionService encryption);

    public async Task InitializeAsync()
    {
        Encryption = new EncryptionService();
        var salt = EncryptionService.GenerateSalt();
        var key = await Encryption.DeriveKeyAsync(TestPassword, salt);
        Encryption.Initialize(key);
        CryptographicOperations.ZeroMemory(key);

        Service = await CreateConnectedServiceAsync(Encryption);
    }

    public async Task DisposeAsync()
    {
        await Service.DisposeAsync();
        Encryption.Dispose();
    }

    #region Connection

    [Fact]
    public void WhenConnected_IsConnectedIsTrue()
    {
        Assert.True(Service.IsConnected);
    }

    #endregion

    #region UploadChunkAsync

    [Fact]
    public async Task UploadChunkAsync_ValidData_ReturnsChunkScopedName()
    {
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);

        var objectKey = await Service.UploadChunkAsync(data, hash);

        Assert.StartsWith("chunks/", objectKey);
        Assert.Contains(hash, objectKey);
    }

    [Fact]
    public async Task UploadChunkAsync_EmptyData_Succeeds()
    {
        var hash = ComputeHash(Array.Empty<byte>());

        var objectKey = await Service.UploadChunkAsync(ReadOnlyMemory<byte>.Empty, hash);

        Assert.StartsWith("chunks/", objectKey);
    }

    [Fact]
    public async Task UploadChunkAsync_EmptyHash_ThrowsSecurityPolicyException()
    {
        var data = CreateRandomContent(1024);

        await Assert.ThrowsAsync<SecurityPolicyException>(() => Service.UploadChunkAsync(data, ""));
    }

    [Fact]
    public async Task UploadChunkAsync_InvalidHashFormat_ThrowsSecurityPolicyException()
    {
        var data = CreateRandomContent(1024);

        await Assert.ThrowsAsync<SecurityPolicyException>(() => Service.UploadChunkAsync(data, "not-a-valid-hash"));
    }

    [Fact]
    public async Task UploadChunkAsync_SameDataTwice_DeduplicatesToSameName()
    {
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);

        var first = await Service.UploadChunkAsync(data, hash);
        var second = await Service.UploadChunkAsync(data, hash);

        Assert.Equal(first, second);
        Assert.True(await Service.BlobExistsAsync(first));
    }

    [Fact]
    public async Task UploadChunkAsync_DifferentData_ProducesDifferentNames()
    {
        var data1 = CreateRandomContent(1024);
        var data2 = CreateRandomContent(1024);

        var key1 = await Service.UploadChunkAsync(data1, ComputeHash(data1));
        var key2 = await Service.UploadChunkAsync(data2, ComputeHash(data2));

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public async Task UploadChunkAsync_WithProgress_ReportsPositiveBytes()
    {
        var data = CreateRandomContent(10 * 1024);
        var hash = ComputeHash(data);
        long reported = 0;
        var progress = new SynchronousProgress<long>(b => reported = b);

        await Service.UploadChunkAsync(data, hash, progress: progress);

        Assert.True(reported > 0);
    }

    [Fact]
    public async Task UploadChunkAsync_IncreasesTotalBytesUploaded()
    {
        var data = CreateRandomContent(1024);
        var before = Service.TotalBytesUploaded;

        await Service.UploadChunkAsync(data, ComputeHash(data));

        Assert.True(Service.TotalBytesUploaded > before);
    }

    [Fact]
    public async Task UploadChunkAsync_IncrementsTotalOperations()
    {
        var data = CreateRandomContent(1024);
        var before = Service.TotalOperations;

        await Service.UploadChunkAsync(data, ComputeHash(data));

        Assert.True(Service.TotalOperations > before);
    }

    #endregion

    #region UploadChunkDirectAsync

    [Fact]
    public async Task UploadChunkDirectAsync_ValidData_ReturnsChunkScopedName()
    {
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);

        var objectKey = await Service.UploadChunkDirectAsync(data, hash);

        Assert.StartsWith("chunks/", objectKey);
        Assert.Contains(hash, objectKey);
    }

    [Fact]
    public async Task UploadChunkDirectAsync_EmptyData_Succeeds()
    {
        var hash = ComputeHash(Array.Empty<byte>());

        var objectKey = await Service.UploadChunkDirectAsync(ReadOnlyMemory<byte>.Empty, hash);

        Assert.StartsWith("chunks/", objectKey);
    }

    [Fact]
    public async Task UploadChunkDirectAsync_InvalidHashFormat_ThrowsSecurityPolicyException()
    {
        var data = CreateRandomContent(1024);

        await Assert.ThrowsAsync<SecurityPolicyException>(() => Service.UploadChunkDirectAsync(data, "tooshort"));
    }

    [Fact]
    public async Task UploadChunkDirectAsync_RoundTripsThroughDownload()
    {
        var data = CreateRandomContent(2048);
        var hash = ComputeHash(data);

        var objectKey = await Service.UploadChunkDirectAsync(data, hash);
        var downloaded = await Service.DownloadChunkAsync(objectKey);

        Assert.Equal(data, downloaded);
    }

    #endregion

    #region DownloadChunkAsync

    [Fact]
    public async Task DownloadChunkAsync_ExistingChunk_ReturnsOriginalData()
    {
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        var objectKey = await Service.UploadChunkAsync(data, hash);

        var downloaded = await Service.DownloadChunkAsync(objectKey);

        Assert.Equal(data, downloaded);
    }

    [Fact]
    public async Task DownloadChunkAsync_NonExistentChunk_ThrowsDataIntegrityException()
    {
        const string validHash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        await Assert.ThrowsAsync<DataIntegrityException>(() => Service.DownloadChunkAsync($"chunks/{validHash}"));
    }

    [Fact]
    public async Task DownloadChunkAsync_InvalidNameFormat_ThrowsSecurityPolicyException()
    {
        await Assert.ThrowsAsync<SecurityPolicyException>(() => Service.DownloadChunkAsync("invalid/path"));
    }

    [Fact]
    public async Task DownloadChunkAsync_EmptyName_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service.DownloadChunkAsync(""));
    }

    [Fact]
    public async Task DownloadChunkStreamingAsync_ExistingChunk_ReturnsOriginalData()
    {
        var data = CreateRandomContent(4096);
        var hash = ComputeHash(data);
        var objectKey = await Service.UploadChunkAsync(data, hash);

        var (buffer, length) = await Service.DownloadChunkStreamingAsync(objectKey);
        try
        {
            Assert.Equal(data.Length, length);
            Assert.Equal(data, buffer.AsSpan(0, length).ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion

    #region Metadata

    [Fact]
    public async Task UploadFileMetadataAsync_NullFile_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Service.UploadFileMetadataAsync(null!));
    }

    [Fact]
    public async Task MetadataRoundTrip_PreservesPathAndSize()
    {
        var file = CreateBackedUpFile(@"C:\contract\test.txt", 1024);
        await Service.UploadFileMetadataAsync(file);
        var blobs = await Service.ListMetadataBlobsAsync();
        var key = blobs.First();

        var downloaded = await Service.DownloadFileMetadataAsync(key);

        Assert.NotNull(downloaded);
        Assert.Equal(file.LocalPath, downloaded!.LocalPath);
        Assert.Equal(file.FileSize, downloaded.FileSize);
    }

    [Fact]
    public async Task ListMetadataBlobsAsync_MultipleFiles_ReturnsAll()
    {
        await Service.UploadFileMetadataAsync(CreateBackedUpFile(@"C:\contract\f1.txt", 1024));
        await Service.UploadFileMetadataAsync(CreateBackedUpFile(@"C:\contract\f2.txt", 2048));
        await Service.UploadFileMetadataAsync(CreateBackedUpFile(@"C:\contract\f3.txt", 4096));

        var blobs = await Service.ListMetadataBlobsAsync();

        Assert.Equal(3, blobs.Count);
    }

    #endregion

    #region Generic blob

    [Fact]
    public async Task GenericBlobRoundTrip_ReturnsStoredBytes()
    {
        var payload = CreateRandomContent(256);
        const string name = "system/index-backup";

        await Service.UploadBlobAsync(name, payload);
        var downloaded = await Service.DownloadBlobAsync(name);

        Assert.Equal(payload, downloaded);
    }

    #endregion

    #region Existence / deletion

    [Fact]
    public async Task BlobExistsAsync_AfterUpload_IsTrue()
    {
        var data = CreateRandomContent(512);
        var objectKey = await Service.UploadChunkAsync(data, ComputeHash(data));

        Assert.True(await Service.BlobExistsAsync(objectKey));
    }

    [Fact]
    public async Task BlobExistsAsync_UnknownName_IsFalse()
    {
        Assert.False(await Service.BlobExistsAsync("chunks/does-not-exist"));
    }

    [Fact]
    public async Task DeleteBlobAsync_ExistingChunk_RemovesIt()
    {
        var data = CreateRandomContent(512);
        var objectKey = await Service.UploadChunkAsync(data, ComputeHash(data));

        await Service.DeleteBlobAsync(objectKey);

        Assert.False(await Service.BlobExistsAsync(objectKey));
    }

    [Fact]
    public async Task DeleteBlobAsync_NonExistent_DoesNotThrow()
    {
        await Service.DeleteBlobAsync("chunks/nonexistent");
    }

    #endregion

    #region Tiering

    [Fact]
    public async Task GetBlobPropertiesAsync_ReflectsUploadTier()
    {
        var data = CreateRandomContent(1024);
        var objectKey = await Service.UploadChunkAsync(data, ComputeHash(data), StorageTier.Cool);

        var (sizeBytes, tier) = await Service.GetBlobPropertiesAsync(objectKey);

        Assert.True(sizeBytes > 0);
        Assert.Equal(StorageTier.Cool, tier);
    }

    [Fact]
    public async Task SetBlobTierAsync_ChangesReportedTier()
    {
        var data = CreateRandomContent(1024);
        var objectKey = await Service.UploadChunkAsync(data, ComputeHash(data), StorageTier.Hot);

        await Service.SetBlobTierAsync(objectKey, StorageTier.Cool);

        var (_, tier) = await Service.GetBlobPropertiesAsync(objectKey);
        Assert.Equal(StorageTier.Cool, tier);
    }

    #endregion

    #region Listing

    [Fact]
    public async Task ListChunkBlobsAsync_ReturnsHashWithoutPrefix()
    {
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        await Service.UploadChunkAsync(data, hash);

        var chunks = await Service.ListChunkBlobsAsync();

        Assert.Contains(hash, chunks);
        Assert.DoesNotContain($"chunks/{hash}", chunks);
    }

    [Fact]
    public async Task ListChunkBlobsWithPropertiesAsync_ReturnsHashSizeAndTier()
    {
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        await Service.UploadChunkAsync(data, hash, StorageTier.Cool);

        var map = await Service.ListChunkBlobsWithPropertiesAsync();

        Assert.True(map.ContainsKey(hash));
        Assert.True(map[hash].sizeBytes > 0);
        Assert.Equal(StorageTier.Cool, map[hash].tier);
    }

    #endregion

    #region Integrity

    [Fact]
    public async Task VerifyChunkIntegrityAsync_MatchingData_ReturnsTrue()
    {
        var data = CreateRandomContent(1024);
        var hash = ComputeHash(data);
        await Service.UploadChunkAsync(data, hash);

        var result = await Service.VerifyChunkIntegrityAsync(hash, data);

        Assert.True(result);
    }

    #endregion

    #region Helpers

    protected static byte[] CreateRandomContent(int size)
    {
        var content = new byte[size];
        RandomNumberGenerator.Fill(content);
        return content;
    }

    protected static string ComputeHash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    protected static BackedUpFile CreateBackedUpFile(string path, long size) => new()
    {
        LocalPath = path,
        BlobName = $"files/{Guid.NewGuid()}",
        FileSize = size,
        LastModified = DateTime.UtcNow,
        FileHash = Guid.NewGuid().ToString("N"),
        Chunks = [new ChunkInfo { Index = 0, Offset = 0, Length = (int)size, Hash = "abc123" }],
        BackedUpAt = DateTime.UtcNow,
        Status = BackupStatus.Completed
    };

    #endregion
}
