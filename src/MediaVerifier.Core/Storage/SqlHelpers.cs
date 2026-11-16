using Microsoft.Data.Sqlite;

namespace MediaVerifier.Core.Storage;

/// <summary>Small helpers for reading columns and binding parameters.</summary>
internal static class SqlHelpers
{
    public static long? GetInt64OrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static int? GetInt32OrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static string? GetStringOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static byte[]? GetBlobOrNull(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (byte[])reader.GetValue(ordinal);

    public static SqliteParameter Add(this SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    /// <summary>Splits ids into chunks of at most 500 so each <c>IN (...)</c> statement shape is reused.</summary>
    public static IEnumerable<ArraySegment<long>> Chunks(long[] ids, int size = 500)
    {
        for (int offset = 0; offset < ids.Length; offset += size)
        {
            yield return new ArraySegment<long>(ids, offset, Math.Min(size, ids.Length - offset));
        }
    }

    /// <summary>
    /// Binds a chunk into a fixed-size parameter list <c>$p0..$p499</c>; unused slots repeat the first id,
    /// so the statement text (and its prepared form) is the same for every chunk.
    /// </summary>
    public static string InList(int size = 500) => string.Join(",", Enumerable.Range(0, size).Select(i => "$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static void BindChunk(SqliteCommand command, ArraySegment<long> chunk, int size = 500)
    {
        for (int i = 0; i < size; i++)
        {
            long value = i < chunk.Count ? chunk[i] : chunk[0];
            command.Parameters.AddWithValue("$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), value);
        }
    }

    /// <summary>
    /// Range bounds that select a directory and everything below it on the (root_id, rel_path) index:
    /// rel_path = p, or p + '\' ≤ rel_path &lt; p + ']' (']' follows '\' in code-point order). Case-exact.
    /// </summary>
    public static (string Exact, string Low, string High) SubtreeBounds(string relPath) =>
        relPath.Length == 0 ? ("", "", "￿") : (relPath, relPath + "\\", relPath + "]");
}
