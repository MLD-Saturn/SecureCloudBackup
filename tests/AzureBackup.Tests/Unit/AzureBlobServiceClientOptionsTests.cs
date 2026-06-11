using Azure.Core;
using AzureBackup.Core.Services;
using Xunit;

namespace AzureBackup.Tests;

/// <summary>
/// W6 Phase 4 (Item 4): pins the <see cref="BlobClientOptions"/> contract that
/// <see cref="AzureBlobService.CreateClientOptions"/> applies to every
/// <see cref="Azure.Storage.Blobs.BlobServiceClient"/> the service constructs.
///
/// <para>
/// The real transport behaviour (connection pooling, multi-Gbps throughput)
/// can only be exercised against a live Azure account, which these unit tests
/// deliberately do not touch. What CAN regress silently is the retry / timeout
/// configuration -- a future edit that drops the exponential backoff or the
/// bounded per-try timeout would weaken resilience without any test noticing.
/// These tests are that guard.
/// </para>
/// </summary>
public class AzureBlobServiceClientOptionsTests
{
    [Fact]
    public void CreateClientOptions_UsesExponentialBackoffRetry()
    {
        var options = AzureBlobService.CreateClientOptions();

        Assert.Equal(RetryMode.Exponential, options.Retry.Mode);
    }

    [Fact]
    public void CreateClientOptions_SetsBoundedRetryCount()
    {
        var options = AzureBlobService.CreateClientOptions();

        Assert.Equal(5, options.Retry.MaxRetries);
    }

    [Fact]
    public void CreateClientOptions_SetsBoundedPerTryNetworkTimeout()
    {
        var options = AzureBlobService.CreateClientOptions();

        Assert.Equal(TimeSpan.FromSeconds(100), options.Retry.NetworkTimeout);
    }

    [Fact]
    public void CreateClientOptions_SetsBackoffDelayWindow()
    {
        var options = AzureBlobService.CreateClientOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(800), options.Retry.Delay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Retry.MaxDelay);
    }

    [Fact]
    public void CreateClientOptions_SuppliesExplicitTransport()
    {
        var options = AzureBlobService.CreateClientOptions();

        Assert.NotNull(options.Transport);
    }

    [Fact]
    public void CreateClientOptions_SharesTheTransportAcrossClients()
    {
        // The transport wraps a process-wide shared HTTP handler, so every
        // client must observe the SAME transport instance -- a per-call
        // transport would defeat the warm-connection-pool optimisation.
        var first = AzureBlobService.CreateClientOptions();
        var second = AzureBlobService.CreateClientOptions();

        Assert.Same(first.Transport, second.Transport);
    }
}
