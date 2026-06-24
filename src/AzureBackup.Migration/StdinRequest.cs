namespace AzureBackup.Migration;

/// <summary>
/// Shared stdin reading for the migration helper's subcommands. Both
/// <see cref="MigrationRunner"/> and <see cref="SeedRunner"/> read a single JSON
/// request line that contains the user's password, so the read must go into a
/// buffer we fully control and can zero -- never a <c>string</c>/<c>StringBuilder</c>,
/// whose internal buffers cannot be reliably cleared.
/// </summary>
internal static class StdinRequest
{
    /// <summary>
    /// Reads all of <paramref name="reader"/> into a <c>char[]</c> we fully
    /// control (and can zero), growing it as needed. Avoids StringBuilder/string,
    /// whose internal buffers cannot be reliably zeroed and would hold a copy of
    /// the password. The caller is responsible for zeroing the returned buffer.
    /// </summary>
    public static char[] ReadAll(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

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

    /// <summary>True if every character in <paramref name="chars"/> is whitespace (or the array is empty).</summary>
    public static bool IsAllWhitespace(char[] chars)
    {
        foreach (var c in chars)
            if (!char.IsWhiteSpace(c)) return false;
        return true;
    }
}
