using AzureBackup.Migration;

// One-time legacy-database migration helper.
//
// Protocol: the launching app writes a single JSON line to this process's STDIN
// describing the migration (legacy db path, salt, password, output snapshot
// path). The password is delivered via stdin -- NEVER argv/env -- so it does not
// leak through process listings. The helper writes a short human-readable status
// line to STDERR and returns a MigrationExitCode as the process exit code.
//
// Usage is intended to be programmatic; there are no command-line arguments.
// The real logic lives in MigrationRunner so it is unit-testable with injected
// reader/writer streams.

return MigrationRunner.Run(Console.In, Console.Error);
