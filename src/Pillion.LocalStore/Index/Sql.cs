using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Index;

/// <summary>Small ADO.NET helpers for the index tables.</summary>
internal static class Sql
{
    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }

    public static long ScalarLong(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) =>
        Convert.ToInt64(Scalar(connection, transaction, sql, parameters) ?? 0L, CultureInfo.InvariantCulture);

    public static bool TableExists(SqliteConnection connection, SqliteTransaction? transaction, string name) =>
        ScalarLong(connection, transaction, "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = $name", ("$name", name)) > 0;

    public static string? ReadMeta(SqliteConnection connection, SqliteTransaction? transaction, string key) =>
        Scalar(connection, transaction, "SELECT value FROM meta WHERE key = $key", ("$key", key)) as string;

    public static void WriteMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value) =>
        Execute(
            connection,
            transaction,
            "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$key", key),
            ("$value", value));

    /// <summary>Adds <c>column IN ($p0, $p1, ...)</c> to a WHERE clause for a non-empty list.</summary>
    public static void AddInFilter(SqliteCommand command, List<string> clauses, string column, IReadOnlyList<string>? values, string prefix)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        var names = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            names[i] = string.Create(CultureInfo.InvariantCulture, $"${prefix}{i}");
            command.Parameters.AddWithValue(names[i], values[i] ?? string.Empty);
        }

        clauses.Add($"{column} IN ({string.Join(", ", names)})");
    }

    public static byte[] ToBlob(ReadOnlySpan<sbyte> vector) => MemoryMarshal.AsBytes(vector).ToArray();

    public static byte[] ToBlob(ReadOnlySpan<float> vector) => MemoryMarshal.AsBytes(vector).ToArray();
}
