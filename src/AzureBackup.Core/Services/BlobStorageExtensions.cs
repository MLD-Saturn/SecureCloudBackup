using System.Collections.Concurrent;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Extension methods for <see cref="IBlobStorageService"/> providing shared high-level operations.
/// Consolidates the parallel metadata-loading pattern used by Restore, Sync, Tier Migration,
/// and Backup Preview into a single implementation.
/// </summary>
public static class BlobStorageExtensions
{
    /// <summary>
    /// Default concurrency for metadata downloads. Each download is a small HTTP GET
    /// (~1–10 KB encrypted JSON), so 32 concurrent requests balance latency hiding
    /// against Azure's per-account rate limit (20,000 req/s).
    /// </summary>
    private const int DefaultMetadataDownloadConcurrency = 32;

    /// <summary>
    /// Downloads all file metadata from Azure in parallel.
    /// This is the canonical implementation used by all tabs that need the full file list
    /// (Sync, Restore, Tier Migration, Backup Preview).
    /// </summary>
    /// <param name="blobService">The blob storage service to use.</param>
    /// <param name="progress">Optional progress reporter: (completed, total).</param>
    /// <param name="concurrency">Maximum parallel metadata downloads.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of all backed-up file metadata entries.</returns>
    public static async Task<List<BackedUpFile>> LoadAllFileMetadataAsync(
        this IBlobStorageService blobService,
        IProgress<(int completed, int total)>? progress = null,
        int concurrency = DefaultMetadataDownloadConcurrency,
        CancellationToken cancellationToken = default)
    {
        var metadataBlobs = await blobService.ListMetadataBlobsAsync(cancellationToken);
        var total = metadataBlobs.Count;

        if (total == 0)
            return [];

        progress?.Report((0, total));

        ConcurrentBag<BackedUpFile> files = [];
        var completed = 0;

        await Parallel.ForEachAsync(
            metadataBlobs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (blobName, ct) =>
            {
                var file = await blobService.DownloadFileMetadataAsync(blobName, ct);
                if (file != null)
                {
                    files.Add(file);
                }

                var count = Interlocked.Increment(ref completed);
                if (count % 50 == 0 || count == total)
                {
                    progress?.Report((count, total));
                }
            });

        return files.ToList();
    }
}
