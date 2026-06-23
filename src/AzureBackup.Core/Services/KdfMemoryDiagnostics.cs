namespace AzureBackup.Core.Services;

/// <summary>
/// Core-side alias for the shared KDF memory helpers, which now live in
/// <see cref="AzureBackup.Crypto.Argon2idDeriver"/>. Kept so the existing
/// <see cref="EncryptionService"/> and <see cref="Backends.SqliteBackend"/> call
/// sites compile unchanged; both methods forward to the single shared
/// implementation so the GC dance and the bytes-to-megabytes conversion cannot
/// drift between Core, the snapshot envelope, and the migration helper.
/// </summary>
internal static class KdfMemoryDiagnostics
{
    /// <summary>Converts a byte count to whole megabytes for diagnostic log lines.</summary>
    public static long ToMegabytes(long bytes) => AzureBackup.Crypto.Argon2idDeriver.ToMegabytes(bytes);

    /// <summary>
    /// Forces a blocking, compacting Large Object Heap collection before the
    /// single Argon2id retry. See
    /// <see cref="AzureBackup.Crypto.Argon2idDeriver.ForceLargeObjectHeapCompaction"/>.
    /// </summary>
    public static void ForceLargeObjectHeapCompaction()
        => AzureBackup.Crypto.Argon2idDeriver.ForceLargeObjectHeapCompaction();
}
