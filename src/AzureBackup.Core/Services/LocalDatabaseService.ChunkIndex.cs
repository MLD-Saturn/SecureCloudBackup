using System.Security.Cryptography;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Chunk index, statistics, and secure reset operations.
/// </summary>
public partial class LocalDatabaseService
{
    #region Chunk Index

    /// <summary>
    /// Gets a chunk index entry by hash.
    /// </summary>
    public ChunkIndexEntry? GetChunkIndexEntry(string chunkHash)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        
        lock (_dbLock)
        {
            return _chunkIndexCollection!.FindOne(x => x.ChunkHash == chunkHash);
        }
    }

    /// <summary>
    /// Saves or updates a chunk index entry.
    /// </summary>
    public void SaveChunkIndexEntry(ChunkIndexEntry entry)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(entry);

        lock (_dbLock)
        {
            var existing = _chunkIndexCollection!.FindOne(x => x.ChunkHash == entry.ChunkHash);
            if (existing != null)
            {
                _chunkIndexCollection.Update(entry);
            }
            else
            {
                _chunkIndexCollection.Insert(entry);
            }
        }
    }

    /// <summary>
    /// Bulk-inserts chunk index entries. Use only after ClearChunkIndex when no existing entries exist.
    /// Significantly faster than individual SaveChunkIndexEntry calls for rebuilds.
    /// </summary>
    public void BulkInsertChunkIndexEntries(IEnumerable<ChunkIndexEntry> entries)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(entries);

        lock (_dbLock)
        {
            _chunkIndexCollection!.InsertBulk(entries);
        }
    }

    /// <summary>
    /// Deletes a chunk index entry.
    /// </summary>
    public void DeleteChunkIndexEntry(string chunkHash)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkHash);
        
        lock (_dbLock)
        {
            _chunkIndexCollection!.DeleteMany(x => x.ChunkHash == chunkHash);
        }
    }

    /// <summary>
    /// Gets all chunk index entries.
    /// </summary>
    public List<ChunkIndexEntry> GetAllChunkIndexEntries()
    {
        EnsureInitialized();

        lock (_dbLock)
        {
            return _chunkIndexCollection!.FindAll().ToList();
        }
    }

    /// <summary>
    /// Gets a lightweight summary of all chunk index entries for fast lookups.
    /// Returns only the hash, reference count, size, and tier — without loading
    /// the ReferencingFiles list, which dominates memory at scale.
    /// At 1M chunks, this uses ~80 MB vs ~1.5 GB for full entries.
    /// </summary>
    public Dictionary<string, (int ReferenceCount, long SizeBytes, StorageTier Tier)> GetChunkIndexSummaryMap()
    {
        EnsureInitialized();

        lock (_dbLock)
        {
            var result = new Dictionary<string, (int, long, StorageTier)>(StringComparer.Ordinal);
            foreach (var entry in _chunkIndexCollection!.FindAll())
            {
                result[entry.ChunkHash] = (entry.ReferenceCount, entry.SizeBytes, entry.CurrentTier);
            }
            return result;
        }
    }

    /// <summary>
    /// Gets chunk entries that reference a specific file.
    /// </summary>
    public List<ChunkIndexEntry> GetChunkEntriesForFile(string filePath)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        
        lock (_dbLock)
        {
            // LiteDB doesn't support querying nested collections well,
            // so we need to filter in memory
            return _chunkIndexCollection!
                .FindAll()
                .Where(e => e.ReferencingFiles.Any(r => 
                    r.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    /// <summary>
    /// Gets orphaned chunks (reference count = 0).
    /// </summary>
    public List<ChunkIndexEntry> GetOrphanedChunks()
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            return _chunkIndexCollection!.Find(x => x.ReferenceCount == 0).ToList();
        }
    }

    /// <summary>
    /// Clears all chunk index entries.
    /// </summary>
    public void ClearChunkIndex()
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            _chunkIndexCollection!.DeleteAll();
        }
    }

    /// <summary>
    /// Gets index metadata by key.
    /// </summary>
    public DateTime? GetIndexMetadata(string key)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        
        lock (_dbLock)
        {
            var entry = _indexMetadataCollection!.FindOne(x => x.Key == key);
            return entry?.Value;
        }
    }

    /// <summary>
    /// Sets index metadata by key.
    /// </summary>
    public void SetIndexMetadata(string key, DateTime value)
    {
        EnsureInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        
        lock (_dbLock)
        {
            var entry = _indexMetadataCollection!.FindOne(x => x.Key == key);
            if (entry != null)
            {
                entry.Value = value;
                _indexMetadataCollection.Update(entry);
            }
            else
            {
                _indexMetadataCollection.Insert(new IndexMetadata { Key = key, Value = value });
            }
        }
    }

    /// <summary>
    /// Gets the total count of chunks in the index.
    /// </summary>
    public int GetChunkIndexCount()
    {
        EnsureInitialized();
        
        lock (_dbLock)
        {
            return _chunkIndexCollection!.Count();
        }
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Gets backup statistics.
    /// </summary>
    public BackupStatistics GetStatistics()
    {
        EnsureInitialized();

        lock (_dbLock)
        {
            var files = _filesCollection!.FindAll().ToList();
            var config = _configCollection!.FindById(1) ?? new BackupConfiguration();

            return new BackupStatistics
            {
                TotalFiles = files.Count,
                TotalSize = files.Sum(x => x.FileSize),
                CompletedFiles = files.Count(x => x.Status == BackupStatus.Completed),
                PendingFiles = files.Count(x => x.Status == BackupStatus.Pending),
                FailedFiles = files.Count(x => x.Status == BackupStatus.Failed),
                PendingChanges = _pendingChangesCollection!.Count(),
                LastBackupTime = config.LastBackupTime,
                TotalBytesUploaded = config.TotalBytesUploaded
            };
        }
    }

    #endregion

    #region Reset and Secure Delete

    /// <summary>
    /// Securely deletes all data and resets the database.
    /// Overwrites sensitive data before deletion to prevent recovery.
    /// After calling this method, the database is closed and the application 
    /// should restart or call Initialize with a new password.
    /// </summary>
    public void SecureReset()
    {
        lock (_dbLock)
        {
            if (_database == null || string.IsNullOrEmpty(_databasePath))
                return;

            // First, overwrite sensitive data in the database
            OverwriteSensitiveData();
            
            // Close the database
            _database.Dispose();
            _database = null;
            _configCollection = null;
            _filesCollection = null;
            _pendingChangesCollection = null;
            _chunkIndexCollection = null;
            _indexMetadataCollection = null;
            
            // Securely delete the database file
            SecureDeleteFile(_databasePath);
            
            // Also delete the journal file if it exists
            var journalPath = _databasePath + "-journal";
            if (File.Exists(journalPath))
            {
                SecureDeleteFile(journalPath);
            }
            
            // Also delete the log file if it exists (LiteDB WAL)
            var logPath = _databasePath + "-log";
            if (File.Exists(logPath))
            {
                SecureDeleteFile(logPath);
            }
            
            // Also delete the salt file
            var saltPath = GetSaltFilePath(_databasePath);
            if (File.Exists(saltPath))
            {
                SecureDeleteFile(saltPath);
            }
            
            Log("SecureReset: Database and salt file have been securely deleted. Application restart required.");
        }
    }

    /// <summary>
    /// Overwrites sensitive data in the database before deletion.
    /// </summary>
    private void OverwriteSensitiveData()
    {
        if (_configCollection == null) return;

        var config = _configCollection.FindById(1);
        if (config != null)
        {
            // Overwrite password-related data
            if (config.PasswordSalt != null)
            {
                RandomNumberGenerator.Fill(config.PasswordSalt);
                config.PasswordSalt = null;
            }

            if (config.PasswordVerificationHash != null)
            {
                RandomNumberGenerator.Fill(config.PasswordVerificationHash);
                config.PasswordVerificationHash = null;
            }
            
            // Overwrite encrypted connection string
            if (config.EncryptedConnectionString != null)
            {
                RandomNumberGenerator.Fill(config.EncryptedConnectionString);
                config.EncryptedConnectionString = null;
            }

            // Reset authentication method to default
            config.AuthMethod = AzureAuthMethod.ConnectionString;

            // Reset Entra ID and storage account settings
            config.StorageAccountName = null;
            config.IsEntraIdAuthenticated = false;
            config.EntraIdUserName = null;

            // Reset other sensitive fields
            config.FailedLoginAttempts = 0;
            config.LockoutUntilUtc = null;
            config.WatchedFolders = [];

            _configCollection.Update(config);
        }

        // Clear all file records
        _filesCollection?.DeleteAll();
        _pendingChangesCollection?.DeleteAll();
        _chunkIndexCollection?.DeleteAll();
        _indexMetadataCollection?.DeleteAll();
    }

    /// <summary>
    /// Securely deletes a file by overwriting with random data before deletion.
    /// </summary>
    private static void SecureDeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return;

        try
        {
            FileInfo fileInfo = new(filePath);
            var fileSize = fileInfo.Length;

            // Overwrite file with random data (3 passes for extra security)
            using (FileStream stream = new(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[4096];
                
                for (var pass = 0; pass < 3; pass++)
                {
                    stream.Position = 0;
                    var remaining = fileSize;
                    
                    while (remaining > 0)
                    {
                        var toWrite = (int)Math.Min(buffer.Length, remaining);
                        RandomNumberGenerator.Fill(buffer.AsSpan(0, toWrite));
                        stream.Write(buffer, 0, toWrite);
                        remaining -= toWrite;
                    }
                    
                    stream.Flush();
                }
            }

            // Now delete the file
            File.Delete(filePath);
        }
        catch (IOException)
        {
            // If secure delete fails, try regular delete
            try { File.Delete(filePath); } catch { /* Best effort */ }
        }
    }

    #endregion
}
