using System.Runtime;
using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace AzureBackup.Crypto;

/// <summary>
/// The single Argon2id key-derivation entry point for the whole solution. Every
/// derived key -- the Azure content key, the database-snapshot key, and the
/// legacy SQLCipher-unlock key in the migration helper -- goes through here so the
/// cost parameters (<see cref="KdfParameters"/>) and the
/// out-of-memory-recovery behaviour cannot drift between call sites.
///
/// <para>
/// <b>OOM handling.</b> Argon2id allocates roughly 8 MB per lane on the Large
/// Object Heap (64 MB total at the canonical parameters). Under LOH fragmentation
/// that allocation can fail with <see cref="OutOfMemoryException"/> even when the
/// OS reports free memory (an observed production failure). This deriver catches
/// the OOM, forces a single blocking LOH compaction, retries ONCE, and only then
/// throws <see cref="InsufficientMemoryForKdfException"/>. The parameters are
/// NEVER silently weakened, because that would change the derived key and lock the
/// user out of their existing data.
/// </para>
/// </summary>
public static class Argon2idDeriver
{
    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary>Converts a byte count to whole megabytes for diagnostic log lines.</summary>
    public static long ToMegabytes(long bytes) => bytes / BytesPerMegabyte;

    /// <summary>
    /// Forces a blocking, compacting Large Object Heap collection. Called once
    /// after an Argon2id <see cref="OutOfMemoryException"/> to defragment the LOH
    /// before the single retry. Never changes the derived key (the Argon2id
    /// parameters are untouched); it only tries to make room for the same
    /// allocation that just failed.
    /// </summary>
    public static void ForceLargeObjectHeapCompaction()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// Derives a key of <see cref="KdfParameters.DerivedKeySize"/> bytes from
    /// <paramref name="password"/> and <paramref name="salt"/> using Argon2id at
    /// the canonical <see cref="KdfParameters"/> cost, with the OOM/LOH-compaction
    /// retry described on the type.
    /// </summary>
    /// <param name="password">The password characters; encoded to UTF-8 internally and zeroed.</param>
    /// <param name="salt">The salt bytes for this key's salt domain.</param>
    /// <param name="keyPurpose">
    /// A short human-readable label (e.g. "database snapshot key") used only in the
    /// diagnostic message of <see cref="InsufficientMemoryForKdfException"/>.
    /// </param>
    /// <param name="diag">Optional sink for verbose timing/memory diagnostic lines.</param>
    /// <exception cref="InsufficientMemoryForKdfException">
    /// Argon2id could not allocate its working memory even after a LOH compaction.
    /// </exception>
    public static byte[] DeriveKey(
        ReadOnlySpan<char> password,
        byte[] salt,
        string keyPurpose,
        Action<string>? diag = null)
    {
        ArgumentNullException.ThrowIfNull(salt);

        var passwordBytes = PasswordBytes.FromChars(password);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        diag?.Invoke($"DeriveKey({keyPurpose}): starting Argon2id (memory={KdfParameters.Argon2MemorySize / 1024} MB, " +
                     $"lanes={KdfParameters.Argon2DegreeOfParallelism}, iterations={KdfParameters.Argon2Iterations})");
        try
        {
            Exception? lastOom;
            try
            {
                return RunArgon2id(passwordBytes, salt);
            }
            catch (OutOfMemoryException ex)
            {
                lastOom = ex;
                diag?.Invoke($"DeriveKey({keyPurpose}): OutOfMemoryException at {sw.ElapsedMilliseconds} ms; " +
                             $"loh fragmented={ToMegabytes(GC.GetGCMemoryInfo().FragmentedBytes)} MB -- compacting and retrying once");
                ForceLargeObjectHeapCompaction();
            }

            try
            {
                var key = RunArgon2id(passwordBytes, salt);
                diag?.Invoke($"DeriveKey({keyPurpose}): completed after LOH compaction in {sw.ElapsedMilliseconds} ms");
                return key;
            }
            catch (OutOfMemoryException ex)
            {
                lastOom = ex;
                diag?.Invoke($"DeriveKey({keyPurpose}): OutOfMemoryException AFTER LOH compaction at {sw.ElapsedMilliseconds} ms -- giving up");
            }

            throw new InsufficientMemoryForKdfException(
                BuildOomDiagnostic(keyPurpose, KdfParameters.Argon2MemorySize), lastOom);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static byte[] RunArgon2id(byte[] passwordBytes, byte[] salt)
    {
        using var argon2 = new Argon2id(passwordBytes)
        {
            Salt = salt,
            DegreeOfParallelism = KdfParameters.Argon2DegreeOfParallelism,
            MemorySize = KdfParameters.Argon2MemorySize,
            Iterations = KdfParameters.Argon2Iterations,
        };
        return argon2.GetBytes(KdfParameters.DerivedKeySize);
    }

    private static string BuildOomDiagnostic(string keyPurpose, int kdfMemoryKb)
    {
        try
        {
            var memInfo = GC.GetGCMemoryInfo();
            var gcMb = ToMegabytes(GC.GetTotalMemory(forceFullCollection: false));
            var totalAvailableMb = ToMegabytes(memInfo.TotalAvailableMemoryBytes);
            return $"Unable to derive the {keyPurpose}: Argon2id key derivation could not allocate " +
                   $"its {kdfMemoryKb / 1024} MB working memory after a forced LOH compaction. " +
                   $"GC managed={gcMb} MB, GC reports {totalAvailableMb} MB available. " +
                   $"If 'available' is high, the cause is Large Object Heap fragmentation. " +
                   $"Close other applications or run the app outside a debugger with Diagnostic Tools open.";
        }
        catch
        {
            return $"Unable to derive the {keyPurpose}: Argon2id key derivation could not allocate " +
                   $"its {kdfMemoryKb / 1024} MB working memory. Close other applications or restart the machine.";
        }
    }
}
