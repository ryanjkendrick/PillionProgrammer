using Microsoft.Data.Sqlite;
using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

/// <summary>
/// Builds index databases for the index storage tests. Index schema version 1 belongs to work item 2.3; these tests make a
/// minimal one (the <c>files</c> table of design section 10) so they run on this branch alone.
/// </summary>
internal static class IndexTestSupport
{
    /// <summary>A stand-in for 2.3's index schema version 1: the <c>files</c> table only.</summary>
    public static StoreMigration Version1 { get; } = StoreMigration.Sql(
        1,
        1,
        "files (test stand-in for work item 2.3)",
        """
        CREATE TABLE files (
            id         INTEGER NOT NULL PRIMARY KEY,
            path       TEXT    NOT NULL UNIQUE,
            language   TEXT    NULL,
            partition  TEXT    NULL,
            hash       INTEGER NULL,
            size       INTEGER NOT NULL DEFAULT 0,
            mtime      INTEGER NOT NULL DEFAULT 0,
            indexed_at INTEGER NULL
        ) STRICT;
        """);

    public static StoreSchema SchemaV1 { get; } = new(IndexStorageSchema.Name, [Version1]);

    public static StoreSchema SchemaV2 { get; } = IndexStorageSchema.After(Version1);

    public static EncryptedDatabaseOptions DatabaseOptions(TempDirectory temp) => new() { DatabasePath = temp.File("index.db") };

    public static Task<IndexStorage> OpenAsync(TempDirectory temp, IndexStorageOptions? options = null) =>
        IndexStorage.OpenAsync(DatabaseOptions(temp), SchemaV2, options);

    public static Task<IndexStorage> OpenWithoutExtensionAsync(TempDirectory temp) =>
        OpenAsync(temp, new IndexStorageOptions { LoadSqliteVec = false });

    public static Task<long> AddFileAsync(this IndexStorage storage, string path, string? language = "csharp", string? partition = "src") =>
        storage.Database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO files (path, language, partition, size, mtime) VALUES ($path, $language, $partition, 0, 0) RETURNING id";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$language", (object?)language ?? DBNull.Value);
            command.Parameters.AddWithValue("$partition", (object?)partition ?? DBNull.Value);
            return (long)(await command.ExecuteScalarAsync(token))!;
        });

    public static Task<long> CountAsync(this IndexStorage storage, string sql) =>
        storage.Database.ReadAsync(async (connection, token) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
        });

    public static Task ExecuteAsync(this IndexStorage storage, string sql) =>
        storage.Database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(token);
        });

    public static NewChunk Chunk(string text, int start = 1, string kind = "method", string breadcrumb = "", sbyte[]? embedding = null) =>
        new(start, start + Math.Max(0, text.Split('\n').Length - 1), kind, breadcrumb, text, Math.Max(1, text.Length / 4))
        {
            Embedding = embedding ?? ReadOnlyMemory<sbyte>.Empty,
        };

    /// <summary>A random unit vector quantised to int8 the way the embedding step does (scale by 127, round).</summary>
    public static sbyte[] RandomVector(Random random, int dims)
    {
        var values = new double[dims];
        var norm = 0.0;
        for (var i = 0; i < dims; i++)
        {
            // Box-Muller: Gaussian components give directions spread evenly over the sphere.
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            values[i] = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            norm += values[i] * values[i];
        }

        norm = Math.Sqrt(norm);
        var vector = new sbyte[dims];
        for (var i = 0; i < dims; i++)
        {
            vector[i] = (sbyte)Math.Clamp(Math.Round(values[i] / norm * 127.0), -127, 127);
        }

        return vector;
    }

    /// <summary>Whether sqlite-vec loads in this test run (the build downloads it, so it should).</summary>
    public static bool SqliteVecAvailable => Pillion.LocalStore.Index.SqliteVec.SqliteVecExtension.Status.IsAvailable;

    public static SqliteConnection OpenWithoutKey(string path) => TestDatabases.OpenWithoutKey(path);
}
