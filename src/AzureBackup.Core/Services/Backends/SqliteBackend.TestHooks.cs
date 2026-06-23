namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// B22: test-only hooks for verifying SQLCipher is loaded, the SQLite engine
/// version, and the schema table count. Lives in its own partial because
/// production code never calls these.
/// </summary>
internal partial class SqliteBackend
{

    /// <summary>
    /// Test hook: returns SQLCipher's reported version, or null if the loaded
    /// SQLite native library is not SQLCipher (i.e. encryption is silently
    /// not happening).
    /// </summary>
    internal string? ReadSqlcipherVersion()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA cipher_version;";
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Test hook: confirms the connection is open by reading SQLite's version
    /// string. Used by the C-1a smoke test to prove end-to-end open + close
    /// works without exposing the connection.
    /// </summary>
    internal string ReadSqliteVersion()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        var result = (string?)cmd.ExecuteScalar();
        return result ?? string.Empty;
    }

    /// <summary>
    /// Test hook: confirms the schema was created by counting expected tables.
    /// </summary>
    internal int CountSchemaTables()
    {
        if (_connection == null)
            throw new InvalidOperationException("Backend is not initialized.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // B22: schema, KDF, and SQLCipher unlock methods moved to
    // SqliteBackend.Schema.cs to keep this file under 400 lines.
}
