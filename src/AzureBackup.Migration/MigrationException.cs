namespace AzureBackup.Migration;

/// <summary>
/// Carries a specific <see cref="MigrationExitCode"/> alongside a message so the
/// helper's entry point can map a failure to the right process exit code while
/// still logging a human-readable reason.
/// </summary>
internal sealed class MigrationException : Exception
{
    public MigrationExitCode ExitCode { get; }

    public MigrationException(MigrationExitCode exitCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
    }
}
