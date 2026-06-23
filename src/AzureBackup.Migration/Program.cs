using System.Text.Json;
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

return Run();

static int Run()
{
    MigrationRequest? request;
    try
    {
        var json = Console.In.ReadToEnd();
        if (string.IsNullOrWhiteSpace(json))
        {
            Console.Error.WriteLine("migration: no request received on stdin");
            return (int)MigrationExitCode.BadRequest;
        }

        request = JsonSerializer.Deserialize(json, MigrationJsonContext.Default.MigrationRequest);
        if (request is null)
        {
            Console.Error.WriteLine("migration: request deserialized to null");
            return (int)MigrationExitCode.BadRequest;
        }
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"migration: invalid request json -- {ex.Message}");
        return (int)MigrationExitCode.BadRequest;
    }

    try
    {
        LegacyCatalogMigrator.Migrate(request);
        Console.Error.WriteLine("migration: success");
        return (int)MigrationExitCode.Success;
    }
    catch (MigrationException ex)
    {
        Console.Error.WriteLine($"migration: failed ({ex.ExitCode}) -- {ex.Message}");
        return (int)ex.ExitCode;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"migration: unexpected error -- {ex.GetType().Name}: {ex.Message}");
        return (int)MigrationExitCode.Unexpected;
    }
}
