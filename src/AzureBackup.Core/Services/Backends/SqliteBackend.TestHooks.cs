namespace AzureBackup.Core.Services.Backends;

/// <summary>
/// B22: test-only hooks for verifying the schema table count. Lives in its own
/// partial because production code never calls these.
/// </summary>
internal partial class SqliteBackend
{
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
