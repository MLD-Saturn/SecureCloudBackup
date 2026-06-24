using System.Text.Json;

namespace AzureBackup.Migration;

/// <summary>
/// Testable core of the migration helper's entry point: reads a
/// <see cref="MigrationRequest"/> from a text reader (stdin), runs the
/// migration, writes a status line to a text writer (stderr), and returns the
/// <see cref="MigrationExitCode"/>. <c>Program.cs</c> is a thin wrapper that
/// passes the real console streams.
/// </summary>
internal static class MigrationRunner
{
    /// <summary>
    /// Reads the request from <paramref name="stdin"/>, performs the migration,
    /// and returns the process exit code. The password is read into a
    /// <c>char[]</c> and zeroed, and the raw input buffers are cleared, so no
    /// un-zeroable <c>string</c> copy of the secret is created.
    /// </summary>
    public static int Run(TextReader stdin, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stderr);

        MigrationRequest? request;
        char[]? jsonChars = null;
        byte[]? jsonBytes = null;
        try
        {
            jsonChars = StdinRequest.ReadAll(stdin);
            if (jsonChars.Length == 0 || StdinRequest.IsAllWhitespace(jsonChars))
            {
                stderr.WriteLine("migration: no request received on stdin");
                return (int)MigrationExitCode.BadRequest;
            }

            jsonBytes = System.Text.Encoding.UTF8.GetBytes(jsonChars);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            request = JsonSerializer.Deserialize<MigrationRequest>(jsonBytes, options);
            if (request is null)
            {
                stderr.WriteLine("migration: request deserialized to null");
                return (int)MigrationExitCode.BadRequest;
            }
        }
        catch (JsonException ex)
        {
            stderr.WriteLine($"migration: invalid request json -- {ex.Message}");
            return (int)MigrationExitCode.BadRequest;
        }
        finally
        {
            if (jsonChars is { Length: > 0 }) Array.Clear(jsonChars);
            if (jsonBytes is { Length: > 0 }) Array.Clear(jsonBytes);
        }

        try
        {
            LegacyCatalogMigrator.Migrate(request);
            stderr.WriteLine("migration: success");
            return (int)MigrationExitCode.Success;
        }
        catch (MigrationException ex)
        {
            stderr.WriteLine($"migration: failed ({ex.ExitCode}) -- {ex.Message}");
            return (int)ex.ExitCode;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"migration: unexpected error -- {ex.GetType().Name}: {ex.Message}");
            return (int)MigrationExitCode.Unexpected;
        }
        finally
        {
            request?.ClearPassword();
        }
    }

    }
