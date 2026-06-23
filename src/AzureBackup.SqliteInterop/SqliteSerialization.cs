using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace AzureBackup.SqliteInterop;

/// <summary>
/// Engine-agnostic helpers for serializing an open SQLite database to a byte
/// image and deserializing one back, plus reaching the raw <see cref="sqlite3"/>
/// handle behind a <see cref="SqliteConnection"/>.
///
/// <para>
/// Shared by the modern-engine in-memory snapshot backend and the legacy
/// SQLCipher migration helper so the fragile, version-sensitive handle
/// reflection and the <c>sqlite3_serialize</c>/<c>sqlite3_deserialize</c>
/// marshaling exist in exactly one place.
/// </para>
/// </summary>
public static class SqliteSerialization
{
    /// <summary>
    /// Serializes the <c>main</c> database of <paramref name="connection"/> to a
    /// managed <c>byte[]</c> via <c>sqlite3_serialize</c>, copying out of the
    /// SQLite-owned buffer and freeing it.
    /// </summary>
    public static byte[] Serialize(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var handle = GetHandle(connection);
        nint ptr = raw.sqlite3_serialize(handle, "main", out long length, 0);
        if (ptr == 0 || length <= 0)
            throw new InvalidOperationException($"sqlite3_serialize failed (ptr={ptr}, len={length}).");
        try
        {
            var image = new byte[length];
            Marshal.Copy(ptr, image, 0, checked((int)length));
            return image;
        }
        finally
        {
            raw.sqlite3_free(ptr);
        }
    }

    /// <summary>
    /// Deserializes <paramref name="image"/> into the <c>main</c> database of the
    /// open <paramref name="connection"/>. The image is copied into a
    /// SQLite-allocated buffer that SQLite owns and frees on connection close
    /// (<c>SQLITE_DESERIALIZE_FREEONCLOSE</c>).
    /// </summary>
    public static void DeserializeInto(SqliteConnection connection, byte[] image)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(image);

        var handle = GetHandle(connection);
        nint buf = raw.sqlite3_malloc(image.Length);
        if (buf == 0)
            throw new InvalidOperationException("sqlite3_malloc failed for snapshot deserialize.");
        // From here SQLite owns 'buf' (FREEONCLOSE), even on error after deserialize.
        Marshal.Copy(image, 0, buf, image.Length);
        int rc = raw.sqlite3_deserialize(
            handle, "main", buf,
            image.Length, image.Length,
            raw.SQLITE_DESERIALIZE_RESIZEABLE | raw.SQLITE_DESERIALIZE_FREEONCLOSE);
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"sqlite3_deserialize failed (rc={rc}).");
    }

    /// <summary>
    /// Reaches the underlying <see cref="sqlite3"/> handle from a
    /// <see cref="SqliteConnection"/>. Microsoft.Data.Sqlite exposes it via an
    /// internal member whose exact name varies by version, so it is located by
    /// TYPE rather than name (the literal "Handle" name is null in some 10.x
    /// builds).
    /// </summary>
    public static sqlite3 GetHandle(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var t = typeof(SqliteConnection);
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance;
        foreach (var p in t.GetProperties(Flags))
            if (typeof(sqlite3).IsAssignableFrom(p.PropertyType) && p.GetValue(connection) is sqlite3 s)
                return s;
        foreach (var f in t.GetFields(Flags))
            if (typeof(sqlite3).IsAssignableFrom(f.FieldType) && f.GetValue(connection) is sqlite3 s)
                return s;
        throw new InvalidOperationException(
            "Could not locate the sqlite3 handle on SqliteConnection (Microsoft.Data.Sqlite internals changed).");
    }
}
