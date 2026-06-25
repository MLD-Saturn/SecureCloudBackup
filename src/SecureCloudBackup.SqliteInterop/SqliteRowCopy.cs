using Microsoft.Data.Sqlite;

namespace SecureCloudBackup.SqliteInterop;

/// <summary>
/// Copies the full schema and all user-table rows from one open SQLite
/// connection into another, regardless of which native engine backs either
/// connection. Used by the legacy-SQLCipher migration helper to copy an
/// encrypted source into a plain in-memory database (a direct
/// <c>BackupDatabase</c> is rejected across the SQLCipher cipher boundary).
///
/// <para>
/// The copy is generic over the schema -- it reflects <c>sqlite_master</c>
/// rather than hard-coding table names -- so it tracks schema changes without
/// edits here.
/// </para>
/// </summary>
public static class SqliteRowCopy
{
    /// <summary>
    /// Replays every CREATE statement from <paramref name="source"/> into
    /// <paramref name="destination"/>, then copies every user-table row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Foreign keys are intentionally left OFF during the row copy.</b> A
    /// fresh <see cref="SqliteConnection"/> defaults to <c>PRAGMA foreign_keys =
    /// OFF</c>, and this method deliberately does NOT turn them on. Bulk-loading
    /// with FK enforcement disabled is the standard SQLite pattern: it avoids
    /// spurious ordering failures (e.g. a child row inserted before its parent,
    /// or circular references) while copying an already-consistent database. The
    /// source database was internally consistent, so the copied data is too --
    /// re-enabling FK checks here would only risk breaking a valid copy. Do NOT
    /// "fix" this by enabling foreign_keys.
    /// </para>
    /// </remarks>
    public static void CopyInto(SqliteConnection source, SqliteConnection destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        // Single pass over sqlite_master: collect both the CREATE statements and
        // the user-table names so we never query the catalog twice.
        var ddl = new List<string>();
        var tableNames = new List<string>();
        using (var cmd = source.CreateCommand())
        {
            cmd.CommandText =
                "SELECT type, name, sql FROM sqlite_master " +
                "WHERE name NOT LIKE 'sqlite_%' " +
                "ORDER BY (type='table') DESC, rootpage;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                var sql = reader.IsDBNull(2) ? null : reader.GetString(2);

                if (!string.IsNullOrWhiteSpace(sql))
                    ddl.Add(sql);
                if (string.Equals(type, "table", StringComparison.Ordinal))
                    tableNames.Add(name);
            }
        }

        foreach (var stmt in ddl)
        {
            using var cmd = destination.CreateCommand();
            cmd.CommandText = stmt;
            cmd.ExecuteNonQuery();
        }

        using var tx = destination.BeginTransaction();
        foreach (var table in tableNames)
            CopyTableRows(source, destination, tx, table);
        tx.Commit();
    }

    private static void CopyTableRows(
        SqliteConnection source, SqliteConnection destination, SqliteTransaction tx, string table)
    {
        using var read = source.CreateCommand();
        read.CommandText = $"SELECT * FROM \"{table}\";";
        using var reader = read.ExecuteReader();

        var fieldCount = reader.FieldCount;
        if (fieldCount == 0) return;

        var columns = new string[fieldCount];
        for (var i = 0; i < fieldCount; i++)
            columns[i] = reader.GetName(i);

        var colList = string.Join(",", columns.Select(c => $"\"{c}\""));
        var paramList = string.Join(",", Enumerable.Range(0, fieldCount).Select(i => $"$p{i}"));

        // Prepare the INSERT once per table and reuse it for every row, rebinding
        // parameter values each iteration. Far cheaper than creating a fresh
        // command and re-parsing the SQL per row for a large catalog.
        using var insert = destination.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = $"INSERT INTO \"{table}\"({colList}) VALUES({paramList});";

        var parameters = new SqliteParameter[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            parameters[i] = insert.CreateParameter();
            parameters[i].ParameterName = $"$p{i}";
            insert.Parameters.Add(parameters[i]);
        }
        insert.Prepare();

        while (reader.Read())
        {
            for (var i = 0; i < fieldCount; i++)
                parameters[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            insert.ExecuteNonQuery();
        }
    }
}
