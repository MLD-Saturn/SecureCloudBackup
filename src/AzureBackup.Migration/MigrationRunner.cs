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
            jsonChars = ReadAll(stdin);
            if (jsonChars.Length == 0 || IsAllWhitespace(jsonChars))
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

    /// <summary>
    /// Reads all of <paramref name="reader"/> into a <c>char[]</c> we fully
    /// control (and can zero), growing it as needed. Avoids StringBuilder/string,
    /// whose internal buffers cannot be reliably zeroed and would hold a copy of
    /// the password.
    /// </summary>
    private static char[] ReadAll(TextReader reader)
    {
        var buffer = new char[8192];
        var length = 0;
        int read;
        while ((read = reader.Read(buffer, length, buffer.Length - length)) > 0)
        {
            length += read;
            if (length == buffer.Length)
            {
                var bigger = new char[buffer.Length * 2];
                Array.Copy(buffer, bigger, length);
                Array.Clear(buffer);
                buffer = bigger;
            }
        }

        if (length == buffer.Length)
            return buffer;

        var result = new char[length];
        Array.Copy(buffer, result, length);
        Array.Clear(buffer);
        return result;
    }

    private static bool IsAllWhitespace(char[] chars)
    {
        foreach (var c in chars)
            if (!char.IsWhiteSpace(c)) return false;
        return true;
    }
}
