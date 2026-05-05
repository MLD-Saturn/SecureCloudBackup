namespace AzureBackup.Core.Models;

/// <summary>
/// Azure Blob Storage access tier for backup data.
/// Affects storage cost and retrieval latency.
/// </summary>
public enum StorageTier
{
    /// <summary>
    /// Hot tier - highest storage cost, lowest access cost.
    /// Best for frequently accessed data.
    /// </summary>
    Hot,
    
    /// <summary>
    /// Cool tier - lower storage cost, higher access cost.
    /// Best for infrequently accessed data (recommended for backups).
    /// </summary>
    Cool,
    
    /// <summary>
    /// Cold tier - even lower storage cost, higher access cost.
    /// Best for rarely accessed data stored for longer periods.
    /// </summary>
    Cold,

    /// <summary>
    /// Archive tier - lowest storage cost, highest access cost and retrieval latency.
    /// Best for long-term retention where hours of retrieval latency is acceptable.
    /// </summary>
    Archive
}

/// <summary>
/// Authentication method for Azure Storage.
/// </summary>
public enum AzureAuthMethod
{
    /// <summary>
    /// Microsoft Entra ID (Azure AD) - for organizational/work accounts.
    /// More secure, no secrets stored locally.
    /// </summary>
    EntraId,
    
    /// <summary>
    /// Connection String with account key - for personal Microsoft accounts.
    /// Connection string is encrypted before storage.
    /// </summary>
    ConnectionString
}

/// <summary>
/// Represents the configuration for Azure storage connection and backup settings.
/// </summary>
public class BackupConfiguration
{
    public int Id { get; set; } = 1;
    
    /// <summary>
    /// The authentication method to use for Azure Storage.
    /// </summary>
    public AzureAuthMethod AuthMethod { get; set; } = AzureAuthMethod.ConnectionString;
    
    /// <summary>
    /// Azure Storage account name (e.g., "mystorageaccount").
    /// Used with Entra ID authentication.
    /// </summary>
    public string? StorageAccountName { get; set; }
    
    /// <summary>
    /// Encrypted Azure connection string. Used with ConnectionString auth method.
    /// Encrypted using the user's derived key after initialization.
    /// </summary>
    public byte[]? EncryptedConnectionString { get; set; }
    
    /// <summary>
    /// Full blob service URI (e.g., "https://mystorageaccount.blob.core.windows.net").
    /// Computed from StorageAccountName if not set directly.
    /// </summary>
    public Uri? BlobServiceUri => !string.IsNullOrEmpty(StorageAccountName) 
        ? new Uri($"https://{StorageAccountName}.blob.core.windows.net") 
        : null;
    
    public string? ContainerName { get; set; } = "backup";
    public List<WatchedFolder> WatchedFolders { get; set; } = [];
    public List<string> GlobalExcludePatterns { get; set; } = [];
    public byte[]? PasswordSalt { get; set; }
    public byte[]? PasswordVerificationHash { get; set; }
    public DateTime? LastBackupTime { get; set; }
    public long TotalBytesUploaded { get; set; }

    /// <summary>
    /// Number of consecutive failed login attempts.
    /// </summary>
    public int FailedLoginAttempts { get; set; }
    
    /// <summary>
    /// Lockout time stored as UTC ticks. Use LockoutUntilUtc property
    /// for convenient access.
    /// </summary>
    public long? LockoutUntilTicks { get; set; }

    /// <summary>
    /// Time until which the account is locked due to failed attempts (UTC).
    /// This is a computed property that converts to/from LockoutUntilTicks.
    /// </summary>
    public DateTime? LockoutUntilUtc
    {
        get => LockoutUntilTicks.HasValue 
            ? new DateTime(LockoutUntilTicks.Value, DateTimeKind.Utc) 
            : null;
        set => LockoutUntilTicks = value?.ToUniversalTime().Ticks;
    }
    
    /// <summary>
    /// Whether the user has successfully authenticated with Entra ID.
    /// Only relevant when AuthMethod is EntraId.
    /// </summary>
    public bool IsEntraIdAuthenticated { get; set; }
    
    /// <summary>
    /// The Entra ID user principal name (email) for display purposes.
    /// </summary>
    public string? EntraIdUserName { get; set; }
    
    /// <summary>
    /// Whether Azure storage is configured (either auth method).
    /// </summary>
    public bool IsAzureConfigured => AuthMethod == AzureAuthMethod.EntraId 
        ? (IsEntraIdAuthenticated && !string.IsNullOrEmpty(StorageAccountName))
        : (EncryptedConnectionString != null);
    
    /// <summary>
    /// Configuration format version for migration support.
    /// </summary>
    public int ConfigVersion { get; set; } = 3; // Bumped for hybrid auth

    /// <summary>
    /// Whether the user has enabled a manual memory limit for backup/restore operations.
    /// When false, the application uses automatic memory management.
    /// <para>
    /// B27: defaults to <c>true</c> (was previously <c>false</c>). The
    /// <c>MemoryBudgetBenchmark</c> sweep on the AMD EPYC 7763 production
    /// hardware showed that even an 8 GB cap has zero throughput cost
    /// against the worst-case <c>media-library-500</c> workload, while
    /// the previous unlimited default allowed the orchestrator to hold
    /// multi-GB working sets on real production backups (see the
    /// 2026-04-23 production log). Existing users keep whatever value
    /// they already set; this change only affects fresh installs and
    /// newly created configuration rows.
    /// </para>
    /// </summary>
    public bool MemoryLimitEnabled { get; set; } = true;

    /// <summary>
    /// The user-selected memory limit in megabytes for backup/restore operations.
    /// Only used when <see cref="MemoryLimitEnabled"/> is true.
    /// Stored as a stepped value (512, 1024, 2048, 4096, 8192, 16384, 32768).
    /// <para>
    /// B27: default raised from 2048 MB to 8192 MB. With the B27 bump
    /// of <c>MaxParallelFileBackups</c> from 8 to 16 the worst-case
    /// in-flight chunk-buffer pressure becomes 16 files x 6 chunks x
    /// 64 MB = 6 GB, so the 8 GB default leaves ~2 GB of headroom for
    /// metadata, GC slack, and short-lived allocations. The
    /// <c>MemoryBudgetBenchmark</c> sweep showed that 4 GB has zero
    /// throughput cost on <c>media-library-500</c> at 8-way file
    /// concurrency, but 4 GB is below the 16-way ceiling so it would
    /// force stalls under the new default. 8 GB is the smallest value
    /// that fits without throttling.
    /// </para>
    /// </summary>
    public int MemoryLimitMB { get; set; } = 8192;
}

/// <summary>
/// Represents a folder being watched for backup.
/// </summary>
public class WatchedFolder
{
    public string Path { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public List<string> ExcludePatterns { get; set; } = [];
    public List<string> ExcludeSubfolders { get; set; } = [];
    
    /// <summary>
    /// The Azure storage tier to use when uploading files from this folder.
    /// Defaults to Hot for standard backup storage.
    /// </summary>
    public StorageTier StorageTier { get; set; } = StorageTier.Hot;
}
