using System.Threading.Channels;
using AzureBackup.Core;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;

namespace AzureBackup.Tests.Infrastructure;

/// <summary>
/// Test helper that wraps ChunkAndStreamChangedAsync to provide the simple
/// (chunks, fileHash) return value that tests need for setup.
/// </summary>
internal static class ChunkingTestHelper
{
    /// <summary>
    /// Chunks a file and returns all chunk metadata and the file hash.
    /// Equivalent to the removed ChunkFileAsync but uses the production ChunkAndStreamChangedAsync pipeline.
    /// </summary>
    internal static async Task<(List<ChunkInfo> Chunks, string FileHash)> ChunkFileForTestAsync(
        this ChunkingService chunkingService,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ChunkPayload>();
        HashSet<string> emptyHashes = [];

        var result = await chunkingService.ChunkAndStreamChangedAsync(
            filePath, emptyHashes, channel.Writer, cdcProgress: null, cancellationToken);

        channel.Writer.Complete();

        // Drain the channel (we don't need the payloads, just the metadata)
        await foreach (var _ in channel.Reader.ReadAllAsync(cancellationToken)) { }

        return result;
    }

    /// <summary>
    /// Computes SHA-256 hash of a file. Replacement for the removed ChunkingService.ComputeFileHashAsync.
    /// </summary>
    internal static Task<string> ComputeFileHashForTestAsync(
        string filePath, CancellationToken cancellationToken = default)
        => HashHelper.ComputeFileHashAsync(filePath, cancellationToken);
}
