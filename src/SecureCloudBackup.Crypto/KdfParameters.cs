namespace SecureCloudBackup.Crypto;

/// <summary>
/// Canonical Argon2id key-derivation parameters and salt size shared by every
/// KDF call site across the solution (the Azure blob-encryption key, the local
/// database-snapshot key, and the legacy SQLCipher-unlock key in the migration
/// helper). Centralizing them turns a hand-maintained "keep these identical"
/// promise into a single source of truth: a divergence between paths would be a
/// silent security regression (one path weakening its work factor with no
/// compile error), which is exactly the failure mode this type removes.
///
/// <para>
/// The different KDF paths still derive <em>different keys</em> from
/// <em>different salt domains</em>. This type fixes only the cost parameters and
/// the salt byte length, not the salts themselves; salt-domain separation is
/// deliberate and unaffected.
/// </para>
/// </summary>
public static class KdfParameters
{
    /// <summary>Argon2id lane count (degree of parallelism).</summary>
    public const int Argon2DegreeOfParallelism = 8;

    /// <summary>Argon2id working-memory size in kibibytes (65,536 KiB = 64 MB).</summary>
    public const int Argon2MemorySize = 65536;

    /// <summary>Argon2id iteration (time) cost.</summary>
    public const int Argon2Iterations = 3;

    /// <summary>Salt length in bytes for every Argon2id derivation in the solution.</summary>
    public const int SaltSize = 16;

    /// <summary>Derived key length in bytes (256-bit key for AES-256-GCM).</summary>
    public const int DerivedKeySize = 32;
}
