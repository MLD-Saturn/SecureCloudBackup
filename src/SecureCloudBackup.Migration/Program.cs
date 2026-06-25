using SecureCloudBackup.Migration;

// One-time legacy-database migration helper.
//
// Protocol: the launching app writes a single JSON line to this process's STDIN
// describing the migration (legacy db path, salt, password, output snapshot
// path). The password is delivered via stdin -- NEVER argv/env -- so it does not
// leak through process listings. The helper writes a short human-readable status
// line to STDERR and returns a MigrationExitCode as the process exit code.
//
// Usage is intended to be programmatic; the only command-line argument is an
// optional subcommand:
//   (none)  -- run the migration (the production launch path; unchanged).
//   seed    -- TEST-ONLY: create a real SQLCipher catalog from a stdin SeedRequest
//              so the cross-process migration integration tests have a genuine
//              source. The main app never invokes this. The password is still
//              stdin-only.
// The real logic lives in MigrationRunner / SeedRunner so it is unit-testable
// with injected reader/writer streams.

if (args.Length > 0 && string.Equals(args[0], "seed", StringComparison.Ordinal))
    return SeedRunner.Run(Console.In, Console.Error);

return MigrationRunner.Run(Console.In, Console.Error);
