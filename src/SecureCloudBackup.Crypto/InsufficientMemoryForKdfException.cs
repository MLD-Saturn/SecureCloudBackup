namespace SecureCloudBackup.Crypto;

/// <summary>
/// Thrown when Argon2id key derivation cannot allocate its working memory even
/// after a Large Object Heap compaction and retry. Deliberately NOT a
/// wrong-password exception -- the user cannot fix this by retyping; they need to
/// free RAM or restart. Callers surface the inner <see cref="OutOfMemoryException"/>
/// details so an environment-specific failure can be diagnosed.
/// </summary>
public sealed class InsufficientMemoryForKdfException : Exception
{
    /// <summary>Creates the exception with a diagnostic message and the underlying OOM cause.</summary>
    public InsufficientMemoryForKdfException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
