using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using SecureCloudBackup.Core.Models;

namespace SecureCloudBackup.Core.Services;

/// <summary>
/// Chunk download, metadata download, and metadata listing operations.
/// </summary>
public partial class AzureBlobService
{
    /// <summary>
    /// Downloads and decrypts a chunk. Uses a single API call to retrieve both
    /// content and Content-MD5 hash for transport integrity verification.
    /// </summary>
    public Task<byte[]> DownloadChunkAsync(string objectKey, CancellationToken cancellationToken = default)
        => DownloadChunkAsync(objectKey, encryptedBufferPool: null, cancellationToken);

    /// <summary>
    /// B77 (W5 hot-path migration follow-up to B71/B73) overload: rent the
    /// encrypted scratch buffer from <paramref name="encryptedBufferPool"/>
    /// when non-null instead of <see cref="ArrayPool{T}.Shared"/>. The
    /// scratch buffer never crosses the channel/orchestrator boundary
    /// (returned in this method's finally block before the call exits) so
    /// routing it through a budget-charged <see cref="ChunkBufferPool"/>
    /// moves the working-set bytes out of the per-core tier cache and into
    /// <see cref="MemoryBudget.UsedBytes"/> for the same reason B72/B73 moved
    /// the plaintext and streaming-download encrypted buffers there. Passing
    /// <see langword="null"/> preserves the pre-B77 behaviour.
    /// </summary>
    public async Task<byte[]> DownloadChunkAsync(string objectKey, ChunkBufferPool? encryptedBufferPool, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        // Validate blob name format
        if (!objectKey.StartsWith("chunks/"))
            throw new SecurityPolicyException("Invalid chunk blob name", SecurityPolicyType.InvalidBlobName);

        var blobClient = _containerClient!.GetBlobClient(objectKey);
        var chunkHash = objectKey["chunks/".Length..];

        try
        {
            // Single API call — returns both content and Content-MD5 in one HTTP response
            var response = await blobClient.DownloadContentAsync(cancellationToken);
            var contentMemory = response.Value.Content.ToMemory();
            var storedContentHash = response.Value.Details.ContentHash;
            TotalOperations++;

            // B77: rent encrypted scratch through the supplied pool when present.
            // Pre-B77 this was always ArrayPool<byte>.Shared.Rent; that path now
            // only fires when encryptedBufferPool is null (best-effort recovery,
            // metadata-only callers, and tests that have no operation-scope pool).
            var rentedEncrypted = RentEncryptedBuffer(contentMemory.Length, encryptedBufferPool);
            try
            {
                // B93: unlike DownloadChunkStreamingAsync, this path is immune to the
                // short-read / stale-pool-tail class of bug because it does NOT use
                // parallel range assembly. `DownloadContentAsync` returns the whole
                // blob body atomically; `contentMemory.Length` is the SDK-confirmed
                // body length, `CopyTo` copies exactly that many bytes into index 0,
                // and the MD5 span below is bounded to the SAME length -- so the
                // possibly-oversized pooled buffer's tail can never enter the hash.
                // No byte-count assertion is needed here; the lengths are equal by
                // construction. Keep it that way: do not "harden" this to mirror the
                // streaming path, that would add a guard that can never fire.
                contentMemory.Span.CopyTo(rentedEncrypted);
                var bytesRead = contentMemory.Length;

                VerifyDownloadIntegrity(rentedEncrypted.AsSpan(0, bytesRead),
                    storedContentHash, objectKey, chunkHash, "DownloadChunkAsync");

                Log(bytesRead > 0
                    ? $"DownloadChunkAsync: Downloaded {objectKey} ({bytesRead:N0} bytes encrypted), decrypting..."
                    : $"DownloadChunkAsync: Downloaded {objectKey} (Empty file? no bytes found), decrypting...");

                try
                {
                    var decrypted = _encryptionService.Decrypt(rentedEncrypted.AsSpan(0, bytesRead));
                    if (bytesRead > 0)
                    {
                        Log($"DownloadChunkAsync: Decrypted {objectKey}: {bytesRead:N0} -> {decrypted.Length:N0} bytes.");
                    }
                    return decrypted;
                }
                catch (Exception ex)
                {
                    Log($"DownloadChunkAsync: DECRYPT FAILED for {objectKey} " +
                        $"(encryptedSize={bytesRead:N0}): {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }
            finally
            {
                ReturnEncryptedBuffer(rentedEncrypted, encryptedBufferPool);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            Log($"DownloadChunkAsync: Chunk not found (404): {objectKey}");
            throw new DataIntegrityException($"Chunk not found: {objectKey}", objectKey, ex);
        }
        catch (RequestFailedException ex)
        {
            Log($"DownloadChunkAsync: Azure request failed for {objectKey}: HTTP {ex.Status} - {ex.Message}");
            // Surface transient failures as the provider-neutral type so the restore
            // retry classifier stays free of any Azure SDK dependency; permanent
            // failures keep their original stack via a bare rethrow.
            if (TryTranslateTransient(ex) is { } transient)
                throw transient;
            throw;
        }
        catch (Exception ex) when (ex is not DataIntegrityException and not SecurityPolicyException)
        {
            Log($"DownloadChunkAsync: EXCEPTION for {objectKey}: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Downloads a chunk and attempts best-effort decryption, skipping CRC32 verification.
    /// Returns null for chunks that are completely unrecoverable.
    /// Used for corrupted file recovery to __corrupted__ subfolder.
    /// </summary>
    public async Task<byte[]?> DownloadChunkBestEffortAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        if (!objectKey.StartsWith("chunks/"))
            throw new SecurityPolicyException("Invalid chunk blob name", SecurityPolicyType.InvalidBlobName);

        var blobClient = _containerClient!.GetBlobClient(objectKey);
        var chunkHash = objectKey["chunks/".Length..];

        try
        {
            var response = await blobClient.DownloadContentAsync(cancellationToken);
            var contentMemory = response.Value.Content.ToMemory();
            TotalOperations++;

            // Rent a buffer for the encrypted data to avoid LOH allocation
            var rentedEncrypted = ArrayPool<byte>.Shared.Rent(contentMemory.Length);
            try
            {
                contentMemory.Span.CopyTo(rentedEncrypted);
                var bytesRead = contentMemory.Length;

                // Record CRC state even though best-effort skips it — this helps
                // confirm whether the CRC was the only problem or if AES-GCM also failed.
                var crcValid = _encryptionService.ValidateCrc(rentedEncrypted.AsSpan(0, bytesRead));

                var decrypted = _encryptionService.DecryptBestEffort(rentedEncrypted.AsSpan(0, bytesRead));
                if (decrypted != null)
                {
                    FileOperationDiagnostics.RecordChunkAmbient("BestEffortDecrypt", chunkHash,
                        decrypted.Length, bytesRead, crcValid: crcValid,
                        extra: "aesGcm=OK");
                    Log($"DownloadChunkBestEffortAsync: Recovered {objectKey} ({bytesRead:N0} -> {decrypted.Length:N0} bytes)");
                }
                else
                {
                    FileOperationDiagnostics.RecordChunkAmbient("BestEffortDecrypt", chunkHash,
                        0, bytesRead, crcValid: crcValid,
                        extra: "aesGcm=FAIL (unrecoverable)");
                    Log($"DownloadChunkBestEffortAsync: {objectKey} is unrecoverable (AES-GCM tag mismatch)");
                }
                return decrypted;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedEncrypted);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            FileOperationDiagnostics.RecordChunkAmbient("BestEffortDecrypt", chunkHash,
                0, extra: "blob=404 (not found)");
            Log($"DownloadChunkBestEffortAsync: Chunk not found: {objectKey}");
            return null;
        }
        catch (Exception ex)
        {
            FileOperationDiagnostics.RecordChunkAmbient("BestEffortDecrypt", chunkHash,
                0, extra: $"error={ex.GetType().Name}: {ex.Message}");
            Log($"DownloadChunkBestEffortAsync: Failed for {objectKey}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// HEAD-only chunk-properties fetch for the integrity-check engine (D1).
    /// Returns ContentLength + ContentHash without transferring any body bytes.
    /// Treats <c>RequestFailedException</c> with status 404 as "blob does not
    /// exist" (returning Exists=false) -- this is the missing-blob signal
    /// the T1 check uses to flag chunk-index-vs-storage drift.
    /// </summary>
    public async Task<(bool Exists, long ContentLength, byte[]? ContentHash)> GetChunkPropertiesAsync(
        string objectKey, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        if (!objectKey.StartsWith("chunks/"))
            throw new SecurityPolicyException("Invalid chunk blob name", SecurityPolicyType.InvalidBlobName);

        var blobClient = _containerClient!.GetBlobClient(objectKey);
        try
        {
            var response = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            TotalOperations++;
            return (true, response.Value.ContentLength, response.Value.ContentHash);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // The "blob does not exist" path is intentionally NOT logged at
            // WARNING level here -- T1 expects this to be a normal outcome
            // for orphan-detection scenarios. The caller (IntegrityCheckService)
            // categorises it as a missing-blob failure with full context.
            return (false, 0, null);
        }
    }

    /// <summary>
    /// Downloads and decrypts a chunk using parallel range downloads for improved throughput.
    /// Uses <see cref="StorageTransferOptions"/> for 8-way parallel block downloads within each chunk.
    /// Both the download buffer and the returned plaintext buffer are rented from
    /// <see cref="ArrayPool{T}"/>, eliminating LOH allocations on the restore hot path.
    /// The caller SHOULD return the plaintext buffer via <c>ArrayPool&lt;byte&gt;.Shared.Return</c>
    /// after writing to disk; non-pooled arrays are accepted harmlessly by Return.
    /// </summary>
    public Task<(byte[] Buffer, int Length)> DownloadChunkStreamingAsync(string objectKey, CancellationToken cancellationToken = default)
        => DownloadChunkStreamingAsync(objectKey, plaintextBufferPool: null, cancellationToken);

    /// <summary>
    /// B71 (W5 Phase 3 Commit 3) overload: rent the plaintext output from the
    /// caller-supplied <see cref="ChunkBufferPool"/> when non-null, otherwise
    /// fall back to <see cref="ArrayPool{T}.Shared"/>. The encrypted download
    /// buffer is still rented from <see cref="ArrayPool{T}.Shared"/> internally
    /// because it never crosses the orchestrator boundary (returned before this
    /// method exits) and the per-core tier-cache retention that the recycler
    /// avoids only matters for buffers whose lifetime spans the channel.
    /// </summary>
    public Task<(byte[] Buffer, int Length)> DownloadChunkStreamingAsync(string objectKey, ChunkBufferPool? plaintextBufferPool, CancellationToken cancellationToken = default)
        => DownloadChunkStreamingAsync(objectKey, plaintextBufferPool, encryptedBufferPool: null, cancellationToken);

    /// <summary>
    /// B73 (W5 Phase 4 Commit 2) overload: in addition to the B71
    /// <paramref name="plaintextBufferPool"/>, also let the caller supply a
    /// <paramref name="encryptedBufferPool"/> for the encrypted scratch buffer.
    /// The encrypted buffer is returned in this method's finally block before
    /// the method exits, so routing it through a budget-charged
    /// <see cref="ChunkBufferPool"/> moves the working-set bytes out of the
    /// pre-B73 unaccounted gap (the <see cref="ArrayPool{T}.Shared"/> per-core
    /// tier cache) and into <see cref="MemoryBudget.UsedBytes"/> for the same
    /// reason B72 moved the plaintext pool's retention there. Passing
    /// <see langword="null"/> for <paramref name="encryptedBufferPool"/>
    /// preserves the pre-B73 behaviour.
    /// </summary>
    public async Task<(byte[] Buffer, int Length)> DownloadChunkStreamingAsync(string objectKey,
        ChunkBufferPool? plaintextBufferPool, ChunkBufferPool? encryptedBufferPool,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        if (!objectKey.StartsWith("chunks/"))
            throw new SecurityPolicyException("Invalid chunk blob name", SecurityPolicyType.InvalidBlobName);

        var blobClient = _containerClient!.GetBlobClient(objectKey);
        var chunkHash = objectKey["chunks/".Length..];

        try
        {
            // Use DownloadToAsync with StorageTransferOptions for parallel range downloads.
            // This enables 8-way concurrent block downloads within a single chunk,
            // significantly improving throughput for chunks > 8 MB.
            var properties = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            var contentLength = properties.Value.ContentLength;
            var storedContentHash = properties.Value.ContentHash;
            TotalOperations++;

            // B73: rent encrypted scratch through the supplied pool when present.
            var rentedEncrypted = RentEncryptedBuffer((int)contentLength, encryptedBufferPool);
            try
            {
                // Download with parallel ranges into a MemoryStream backed by the rented buffer
                using var memoryStream = new MemoryStream(rentedEncrypted, 0, (int)contentLength, writable: true);
                var downloadOptions = new BlobDownloadToOptions
                {
                    TransferOptions = DefaultTransferOptions
                };
                await blobClient.DownloadToAsync(memoryStream, downloadOptions, cancellationToken);
                var bytesRead = (int)memoryStream.Position;

                // B93: the streaming path assembles the blob from up to 8 parallel
                // range requests into one caller-supplied MemoryStream over a POOLED
                // (and therefore possibly-stale, non-zeroed) buffer. If the parallel
                // assembly ends with the stream position NOT exactly equal to the
                // blob's content length -- a short read the SDK did not surface as an
                // error, a range that silently under-delivered, or an off-by-a-block
                // accounting slip -- then the MD5 below would run over the WRONG number
                // of bytes (or over leftover bytes from the pool's previous tenant) and
                // fail deterministically, masquerading as "data corrupted at rest" when
                // the blob in Azure is actually fine. Catch that here and surface it as
                // a retryable DownloadIntegrityException so the restore pipeline
                // re-downloads the chunk (a fresh assembly almost always succeeds)
                // instead of failing the file and zero-filling a perfectly good chunk.
                if (!IsAssembledLengthComplete(bytesRead, contentLength))
                {
                    FileOperationDiagnostics.RecordChunkAmbient("ShortRead", chunkHash,
                        0, bytesRead,
                        extra: $"expectedLen={contentLength}, gotLen={bytesRead} (parallel-range assembly incomplete)");
                    Log($"DownloadChunkStreamingAsync: SHORT/OVER READ for {objectKey} -- " +
                        $"expected {contentLength:N0} bytes, assembled {bytesRead:N0}; retrying as transient");
                    throw new DownloadIntegrityException(
                        $"Download incomplete for {objectKey} - assembled {bytesRead:N0} of {contentLength:N0} bytes", objectKey);
                }

                VerifyDownloadIntegrity(rentedEncrypted.AsSpan(0, bytesRead),
                    storedContentHash, objectKey, chunkHash, "DownloadChunkStreamingAsync");

                Log($"DownloadChunkStreamingAsync: Downloaded {objectKey} ({bytesRead:N0} bytes encrypted), decrypting...");

                // Decrypt into a rented plaintext buffer — eliminates the LOH allocation
                // that Decrypt() would make internally. The caller returns this buffer
                // to the pool after writing to disk and hashing.
                var plaintextSize = bytesRead - EncryptionService.EncryptionOverhead;
                byte[] rentedPlaintext = plaintextBufferPool is null
                    ? ArrayPool<byte>.Shared.Rent(plaintextSize)
                    : plaintextBufferPool.Rent(plaintextSize).Buffer;
                try
                {
                    var decryptedLength = _encryptionService.DecryptInto(
                        rentedEncrypted.AsSpan(0, bytesRead), rentedPlaintext);

                    Log($"DownloadChunkStreamingAsync: Decrypted {objectKey}: {bytesRead:N0} -> {decryptedLength:N0} bytes");
                    return (rentedPlaintext, decryptedLength);
                }
                catch
                {
                    // Decrypt failed — return the plaintext buffer we rented (caller won't see it)
                    if (plaintextBufferPool is null)
                        ArrayPool<byte>.Shared.Return(rentedPlaintext, clearArray: true);
                    else
                        plaintextBufferPool.Return(rentedPlaintext);
                    throw;
                }
            }
            finally
            {
                ReturnEncryptedBuffer(rentedEncrypted, encryptedBufferPool);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            Log($"DownloadChunkStreamingAsync: Chunk not found (404): {objectKey}");
            throw new DataIntegrityException($"Chunk not found: {objectKey}", objectKey, ex);
        }
        catch (RequestFailedException ex)
        {
            Log($"DownloadChunkStreamingAsync: Azure request failed for {objectKey}: HTTP {ex.Status} - {ex.Message}");
            // See DownloadChunkAsync: translate transient failures to the neutral
            // type for the retry classifier; rethrow permanent failures unchanged.
            if (TryTranslateTransient(ex) is { } transient)
                throw transient;
            throw;
        }
        catch (Exception ex) when (ex is not DataIntegrityException and not SecurityPolicyException)
        {
            Log($"DownloadChunkStreamingAsync: EXCEPTION for {objectKey}: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Lists all backed up files by retrieving metadata blobs.
    /// </summary>
    public async Task<List<string>> ListMetadataKeysAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        List<string> blobs = new();

        await foreach (var page in _containerClient!
            .GetBlobsAsync(prefix: "metadata/", cancellationToken: cancellationToken)
            .AsPages(pageSizeHint: 5000))
        {
            foreach (var blob in page.Values)
            {
                blobs.Add(blob.Name);
            }
        }

        TotalOperations++;
        return blobs;
    }

    /// <summary>
    /// Downloads and decrypts file metadata.
    /// </summary>
    public async Task<BackedUpFile?> DownloadFileMetadataAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        try
        {
            var blobClient = _containerClient!.GetBlobClient(objectKey);

            // Single API call to get content (faster than GetProperties + DownloadTo)
            var response = await blobClient.DownloadContentAsync(cancellationToken);

            var encryptedMemory = response.Value.Content.ToMemory();
            TotalOperations++;

            // Decrypt into a pooled buffer so the plaintext (which contains file paths
            // and chunk hashes) does not leave fresh allocations on the managed heap
            // after this call returns. The UTF-8 string built below is the only
            // long-lived plaintext artefact; the buffer itself is zeroed-on-return.
            var plaintextMax = encryptedMemory.Length - EncryptionService.EncryptionOverhead;
            if (plaintextMax <= 0)
                throw new DataIntegrityException($"Metadata blob too short to decrypt: {objectKey}", objectKey);

            var rentedPlaintext = ArrayPool<byte>.Shared.Rent(plaintextMax);
            string json;
            try
            {
                var plaintextLength = _encryptionService.DecryptInto(
                    encryptedMemory.Span, rentedPlaintext.AsSpan(0, plaintextMax));
                json = System.Text.Encoding.UTF8.GetString(rentedPlaintext.AsSpan(0, plaintextLength));
            }
            finally
            {
                // clearArray: true — sensitive metadata (paths, hashes) must not
                // linger in pooled buffers for a future consumer to peek at.
                ArrayPool<byte>.Shared.Return(rentedPlaintext, clearArray: true);
            }

            var metadata = JsonSerializer.Deserialize<MetadataDto>(json);
            if (metadata == null)
                throw new DataIntegrityException($"Failed to deserialize metadata for {objectKey}", objectKey);

            // Validate chunk sequence
            var sortedChunks = metadata.Chunks.OrderBy(c => c.Index).ToList();
            for (int i = 0; i < sortedChunks.Count; i++)
            {
                if (sortedChunks[i].Index != i)
                    throw new DataIntegrityException(
                        $"Invalid chunk sequence in {objectKey}: expected index {i}, found {sortedChunks[i].Index}", 
                        objectKey);
            }

            // Storage tier is not retrieved in single-call mode (would require separate GetProperties)
            // This is acceptable since tier info is optional for metadata listing
            StorageTier? storageTier = null;

            return new BackedUpFile
            {
                LocalPath = metadata.LocalPath,
                FileSize = metadata.FileSize,
                LastModified = metadata.LastModified,
                FileHash = metadata.FileHash,
                MetadataVersion = metadata.Version,
                Chunks = sortedChunks.Select(c => new ChunkInfo
                {
                    Index = c.Index,
                    Hash = c.Hash,
                    Offset = c.Offset,
                    Length = c.Length,
                    BlobName = $"chunks/{c.Hash}"
                }).ToList(),
                Status = BackupStatus.Completed,
                CurrentStorageTier = storageTier
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null; // Blob doesn't exist - acceptable
        }
        catch (DataIntegrityException)
        {
            throw; // Re-throw our custom exceptions
        }
        catch (JsonException ex)
        {
            throw new DataIntegrityException($"Metadata corrupted for {objectKey}", objectKey, ex);
        }
    }
}
