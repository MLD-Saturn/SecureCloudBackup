using System.Buffers;
using SecureCloudBackup.Core.Models;
using SecureCloudBackup.Core.Services;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// B73 (W5 Phase 4 Commit 2): tests for the encrypted-buffer routing on the
/// upload + download blob-service paths. The direct contract for
/// <see cref="AzureBlobService.RentEncryptedBuffer"/> /
/// <see cref="AzureBlobService.ReturnEncryptedBuffer"/> is pinned at the
/// helper level so it can be exercised without an Azure connection; the
/// in-memory blob service's parameter-only support is pinned end-to-end
/// to prove the new <see cref="IObjectStorageService"/> overloads round-trip
/// data unchanged.
/// </summary>
public sealed class EncryptedBufferPoolRoutingTests
{
    private const int MB = 1024 * 1024;

    [Fact]
    public void RentEncryptedBuffer_NullPool_RentsFromArrayPoolShared()
    {
        var buffer = AzureBlobService.RentEncryptedBuffer(1024, pool: null);

        Assert.NotNull(buffer);
        Assert.True(buffer.Length >= 1024);

        // Symmetric return must not throw.
        AzureBlobService.ReturnEncryptedBuffer(buffer, pool: null);
    }

    [Fact]
    public void RentEncryptedBuffer_WithPool_RoutesThroughPool()
    {
        using var pool = new ChunkBufferPool(ChunkBufferPool.SmallChunkBucketSizes);

        var rentsBefore = pool.TotalRents;
        var buffer = AzureBlobService.RentEncryptedBuffer(64 * 1024, pool);

        Assert.Equal(rentsBefore + 1, pool.TotalRents);
        Assert.Equal(64 * 1024, buffer.Length);

        var returnsBefore = pool.TotalReturns;
        AzureBlobService.ReturnEncryptedBuffer(buffer, pool);

        Assert.Equal(returnsBefore + 1, pool.TotalReturns);
        Assert.Equal(1, pool.TotalReturnsAccepted);
    }

    [Fact]
    public void RentEncryptedBuffer_PoolBufferReturnedRoutesBack_NextRentServedFromCache()
    {
        using var pool = new ChunkBufferPool(ChunkBufferPool.SmallChunkBucketSizes);

        var first = AzureBlobService.RentEncryptedBuffer(64 * 1024, pool);
        AzureBlobService.ReturnEncryptedBuffer(first, pool);

        var second = AzureBlobService.RentEncryptedBuffer(64 * 1024, pool);
        Assert.Same(first, second);
        Assert.Equal(1, pool.TotalRentsFromPool);

        AzureBlobService.ReturnEncryptedBuffer(second, pool);
    }

    [Fact]
    public void RentEncryptedBuffer_PoolWithBudget_RetentionChargesAndReleases()
    {
        using var budget = new MemoryBudget(128L * MB);
        using var pool = new ChunkBufferPool(ChunkBufferPool.SmallChunkBucketSizes, long.MaxValue, budget);

        var buffer = AzureBlobService.RentEncryptedBuffer(64 * 1024, pool);
        Assert.Equal(0, budget.UsedBytes); // not yet returned

        AzureBlobService.ReturnEncryptedBuffer(buffer, pool);
        Assert.Equal(64 * 1024L, budget.UsedBytes); // now retained in pool, charged

        var again = AzureBlobService.RentEncryptedBuffer(64 * 1024, pool);
        Assert.Same(buffer, again);
        Assert.Equal(0, budget.UsedBytes); // rent-from-cache released the retention

        AzureBlobService.ReturnEncryptedBuffer(again, pool);
    }

    [Fact]
    public async Task UploadChunkAsync_WithExplicitEncryptedPool_RoundTripsData()
    {
        // The in-memory blob service ignores the encrypted-pool parameter (its stored
        // payload IS the encrypted bytes), but the B73 contract requires that supplying
        // the parameter does not break correctness. This is the smoke test for that.
        using var encryption = new EncryptionService();
        var salt = EncryptionService.GenerateSalt();
        var key = await encryption.DeriveKeyAsync("password", salt);
        encryption.Initialize(key);

        await using var blob = new InMemoryBlobService(encryption);
        await blob.ConnectAsync("UseDevelopmentStorage=true", "test");

        using var pool = new ChunkBufferPool(ChunkBufferPool.SmallChunkBucketSizes);

        var payload = new byte[256 * 1024];
        new Random(42).NextBytes(payload);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));

        var objectKey = await blob.UploadChunkAsync(payload, hash, pool, StorageTier.Hot);
        Assert.Equal($"chunks/{hash}", objectKey);

        // Round-trip via the new 3-pool download overload, also passing the pool.
        var (decrypted, length) = await blob.DownloadChunkStreamingAsync(
            objectKey, plaintextBufferPool: pool, encryptedBufferPool: pool);
        try
        {
            Assert.Equal(payload.Length, length);
            Assert.True(payload.AsSpan().SequenceEqual(decrypted.AsSpan(0, length)));
        }
        finally
        {
            pool.Return(decrypted);
        }
    }

    [Fact]
    public async Task UploadChunkDirectAsync_WithExplicitEncryptedPool_RoundTripsData()
    {
        using var encryption = new EncryptionService();
        var salt = EncryptionService.GenerateSalt();
        var key = await encryption.DeriveKeyAsync("password", salt);
        encryption.Initialize(key);

        await using var blob = new InMemoryBlobService(encryption);
        await blob.ConnectAsync("UseDevelopmentStorage=true", "test");

        using var pool = new ChunkBufferPool(ChunkBufferPool.LargeChunkBucketSizes);

        var payload = new byte[20 * MB];
        new Random(7).NextBytes(payload);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));

        var objectKey = await blob.UploadChunkDirectAsync(payload, hash, pool, StorageTier.Hot);
        Assert.Equal($"chunks/{hash}", objectKey);

        var roundTripped = await blob.DownloadChunkAsync(objectKey);
        Assert.Equal(payload.Length, roundTripped.Length);
        Assert.True(payload.AsSpan().SequenceEqual(roundTripped));
    }
}
