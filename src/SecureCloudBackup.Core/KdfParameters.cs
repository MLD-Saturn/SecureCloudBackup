namespace SecureCloudBackup.Core;

/// <summary>
/// Core-side re-export of the canonical Argon2id key-derivation parameters,
/// which now live in <see cref="SecureCloudBackup.Crypto.KdfParameters"/> so the
/// engine-agnostic crypto library, the migration helper, and Core all share one
/// source of truth. This type is kept (forwarding to the shared constants) so
/// existing Core call sites (<c>using static SecureCloudBackup.Core.KdfParameters</c>)
/// compile unchanged; a divergence between paths would be a silent security
/// regression (one path weakening its work factor with no compile error), which
/// is exactly the failure mode the single source of truth removes.
///
/// <para>
/// The different KDF paths still derive <em>different keys</em> from
/// <em>different salt domains</em> (the plaintext <c>.salt</c> sidecar unlocks
/// the local database; the in-database <c>config.password_salt</c> derives the
/// Azure key; the snapshot embeds its own salt). This type fixes only the cost
/// parameters and the salt byte length, not the salts themselves.
/// </para>
/// </summary>
internal static class KdfParameters
{
    /// <summary>Argon2id lane count (degree of parallelism).</summary>
    public const int Argon2DegreeOfParallelism = Crypto.KdfParameters.Argon2DegreeOfParallelism;

    /// <summary>Argon2id working-memory size in kibibytes (65,536 KiB = 64 MB).</summary>
    public const int Argon2MemorySize = Crypto.KdfParameters.Argon2MemorySize;

    /// <summary>Argon2id iteration (time) cost.</summary>
    public const int Argon2Iterations = Crypto.KdfParameters.Argon2Iterations;

    /// <summary>Salt length in bytes for both the local-unlock and Azure-key derivations.</summary>
    public const int SaltSize = Crypto.KdfParameters.SaltSize;
}
