using Azure;
using SecureCloudBackup.Core;
using SecureCloudBackup.Core.Services;
using Xunit;

namespace SecureCloudBackup.Tests;

/// <summary>
/// Pins the provider-neutral storage-exception translation introduced for the
/// object-storage abstraction: <see cref="AzureBlobService"/> converts a
/// transient Azure <see cref="RequestFailedException"/> into a
/// <see cref="TransientStorageException"/> at its boundary (so
/// <c>RestoreService</c>'s retry classifier needs no Azure SDK dependency) and
/// leaves permanent failures for the caller to handle. The transient code set
/// must stay exactly the HTTP codes the restore retry loop previously matched
/// on the raw Azure exception (408/429/500/502/503/504).
/// </summary>
public class StorageExceptionTranslationTests
{
    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void IsTransientStatus_TransientCodes_ReturnsTrue(int status)
    {
        Assert.True(AzureBlobService.IsTransientStatus(status));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    public void IsTransientStatus_NonTransientCodes_ReturnsFalse(int status)
    {
        Assert.False(AzureBlobService.IsTransientStatus(status));
    }

    [Fact]
    public void TryTranslateTransient_TransientStatus_ReturnsTransientStorageExceptionWrappingOriginal()
    {
        var requestFailed = new RequestFailedException(503, "Service Unavailable");

        var translated = AzureBlobService.TryTranslateTransient(requestFailed);

        Assert.NotNull(translated);
        Assert.Equal(503, translated!.Status);
        Assert.Same(requestFailed, translated.InnerException);
    }

    [Fact]
    public void TryTranslateTransient_PermanentStatus_ReturnsNull()
    {
        var requestFailed = new RequestFailedException(403, "Forbidden");

        Assert.Null(AzureBlobService.TryTranslateTransient(requestFailed));
    }

    [Fact]
    public void TransientStorageException_IsAStorageException()
    {
        var ex = new TransientStorageException("boom", 503, new InvalidOperationException());

        Assert.IsAssignableFrom<StorageException>(ex);
        Assert.Equal(503, ex.Status);
    }
}
