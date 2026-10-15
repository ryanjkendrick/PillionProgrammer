using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Index;

/// <summary>
/// The vector rows of one index database, shared by every <see cref="IVectorIndex"/> backend: the int8 vectors in
/// <c>chunk_embeddings</c>, the development-only float32 vectors in <c>vec_float32</c>, and the bounded in-memory copy
/// of the int8 vectors that the default backend searches.
/// </summary>
internal sealed class VectorStorage
{
    private readonly object _cacheLock = new();
    private VectorCache? _cache;
    private long _overLimitGeneration = -1;

    public VectorStorage(EncryptedDatabase database, long cacheMaxBytes = 0)
    {
        Database = database;
        CacheMaxBytes = cacheMaxBytes;
    }

    public EncryptedDatabase Database { get; }

    /// <summary>Gets the largest in-memory copy of the vectors kept; 0 for none.</summary>
    public long CacheMaxBytes { get; }

    /// <summary>Gets the size of the in-memory copy of the vectors, or 0 when there is none.</summary>
    public long CacheBytes => Volatile.Read(ref _cache)?.Bytes ?? 0;

    /// <summary>Starts writing vector rows inside a write transaction.</summary>
    public static VectorWriteSession BeginWrite(SqliteConnection connection, SqliteTransaction transaction) => new(connection, transaction);

    /// <summary>
    /// Gets the in-memory copy of the vectors, loading it (or reloading it after any change to <c>chunk_embeddings</c>)
    /// on this reader connection; <see langword="null"/> when the vectors do not fit <see cref="CacheMaxBytes"/>.
    /// </summary>
    public VectorCache? GetCache(SqliteConnection connection, int dimensions)
    {
        if (CacheMaxBytes <= 0)
        {
            return null;
        }

        var generation = VectorCache.ReadGeneration(connection, null);
        var cache = Volatile.Read(ref _cache);
        if (cache is not null && cache.Generation == generation && cache.Dimensions == dimensions)
        {
            return cache;
        }

        if (Interlocked.Read(ref _overLimitGeneration) == generation)
        {
            return null;
        }

        lock (_cacheLock)
        {
            cache = _cache;
            if (cache is not null && cache.Generation == generation && cache.Dimensions == dimensions)
            {
                return cache;
            }

            var count = Sql.ScalarLong(connection, null, "SELECT count(*) FROM chunk_embeddings");
            if (count * dimensions > CacheMaxBytes)
            {
                Volatile.Write(ref _cache, null);
                Interlocked.Exchange(ref _overLimitGeneration, generation);
                return null;
            }

            try
            {
                cache = VectorCache.Load(connection, dimensions, (int)count);
            }
            catch (InvalidOperationException)
            {
                cache = null;
            }

            Volatile.Write(ref _cache, cache);
            return cache;
        }
    }

    internal static int? ReadDimensions(SqliteConnection connection, SqliteTransaction? transaction) =>
        int.TryParse(Sql.ReadMeta(connection, transaction, IndexStorageSchema.MetaKeys.VectorDimensions), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dims) && dims > 0
            ? dims
            : null;
}

/// <summary>Vector writes within one write transaction; prepared commands are reused across rows.</summary>
internal sealed class VectorWriteSession : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly List<SqliteCommand> _commands = [];
    private int? _dimensions;
    private SqliteCommand? _upsertPlain;
    private SqliteCommand? _upsertFloat32;

    public VectorWriteSession(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
        _dimensions = VectorStorage.ReadDimensions(connection, transaction);
    }

    /// <summary>Stores (or replaces) the int8 vector of a chunk. The first vector stored fixes the index's dimension.</summary>
    /// <param name="chunkId">The chunk; it must exist.</param>
    /// <param name="vector">The vector.</param>
    /// <param name="language">The language of the chunk's file.</param>
    /// <param name="partition">The partition of the chunk's file.</param>
    public void Upsert(long chunkId, ReadOnlySpan<sbyte> vector, string? language, string? partition)
    {
        EnsureDimensions(vector.Length);
        _upsertPlain ??= Prepare(
            "INSERT INTO chunk_embeddings (chunk_id, language, partition, embedding) VALUES ($id, $language, $partition, $embedding) "
            + "ON CONFLICT (chunk_id) DO UPDATE SET language = excluded.language, partition = excluded.partition, embedding = excluded.embedding",
            "$id",
            "$language",
            "$partition",
            "$embedding");
        Run(_upsertPlain, chunkId, language ?? string.Empty, partition ?? string.Empty, Sql.ToBlob(vector));
    }

    /// <summary>Stores the development-only float32 vector of a chunk.</summary>
    /// <param name="chunkId">The chunk.</param>
    /// <param name="vector">The vector.</param>
    public void UpsertFloat32(long chunkId, ReadOnlySpan<float> vector)
    {
        _upsertFloat32 ??= Prepare(
            "INSERT INTO vec_float32 (chunk_id, embedding) VALUES ($id, $embedding) ON CONFLICT (chunk_id) DO UPDATE SET embedding = excluded.embedding",
            "$id",
            "$embedding");
        Run(_upsertFloat32, chunkId, Sql.ToBlob(vector));
    }

    /// <summary>Removes the vectors of a file's chunks, leaving the chunks.</summary>
    /// <param name="fileId">The file.</param>
    /// <returns>The number of int8 vectors removed.</returns>
    public int DeleteByFile(long fileId)
    {
        Sql.Execute(_connection, _transaction, "DELETE FROM vec_float32 WHERE chunk_id IN (SELECT id FROM chunks WHERE file_id = $file)", ("$file", fileId));
        return Sql.Execute(_connection, _transaction, "DELETE FROM chunk_embeddings WHERE chunk_id IN (SELECT id FROM chunks WHERE file_id = $file)", ("$file", fileId));
    }

    public void Dispose()
    {
        foreach (var command in _commands)
        {
            command.Dispose();
        }
    }

    private static void Run(SqliteCommand command, params object[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters[i].Value = values[i];
        }

        command.ExecuteNonQuery();
    }

    private void EnsureDimensions(int length)
    {
        if (length <= 0)
        {
            throw new ArgumentException("A vector needs at least one dimension.");
        }

        if (_dimensions is { } dims)
        {
            if (dims != length)
            {
                throw new ArgumentException($"The index stores {dims}-dimension vectors; this one has {length}. A different embedding model needs a new index.");
            }

            return;
        }

        Sql.WriteMeta(_connection, _transaction, IndexStorageSchema.MetaKeys.VectorDimensions, length.ToString(CultureInfo.InvariantCulture));
        _dimensions = length;
    }

    private SqliteCommand Prepare(string sql, params string[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var name in parameters)
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }

        _commands.Add(command);
        return command;
    }
}
