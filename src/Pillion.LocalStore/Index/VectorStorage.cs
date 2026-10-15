using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Index;

/// <summary>
/// The vector rows of one index database, shared by both <see cref="IVectorIndex"/> backends: the int8 vectors in
/// <c>chunk_embeddings</c> (always written, readable by every host), the sqlite-vec <c>chunk_vectors</c> table (written
/// when this process loaded the extension, otherwise marked stale or told which rows to drop through <c>vec_deleted</c>),
/// and the development-only float32 vectors in <c>vec_float32</c>.
/// </summary>
internal sealed class VectorStorage
{
    /// <summary>The value of <see cref="IndexStorageSchema.MetaKeys.VectorIndexState"/> while <c>chunk_vectors</c> matches <c>chunk_embeddings</c>.</summary>
    public const string Synced = "synced";

    /// <summary>The value after a host without sqlite-vec changed vectors.</summary>
    public const string Stale = "stale";

    public VectorStorage(EncryptedDatabase database, bool extensionLoaded)
    {
        Database = database;
        ExtensionLoaded = extensionLoaded;
    }

    public EncryptedDatabase Database { get; }

    /// <summary>Gets a value indicating whether every connection of <see cref="Database"/> has sqlite-vec loaded.</summary>
    public bool ExtensionLoaded { get; }

    /// <summary>Starts writing vector rows inside a write transaction.</summary>
    public VectorWriteSession BeginWrite(SqliteConnection connection, SqliteTransaction transaction) => new(this, connection, transaction);

    /// <summary>
    /// Brings <c>chunk_vectors</c> up to date when this process has the extension: creates it once the dimension is known,
    /// rebuilds it from <c>chunk_embeddings</c> when it is stale, and drops the rows of deleted chunks. Takes the write lock
    /// only when there is something to do.
    /// </summary>
    /// <returns>What was done.</returns>
    public async Task<VectorMaintenance> MaintainAsync(CancellationToken cancellationToken)
    {
        if (!ExtensionLoaded)
        {
            return VectorMaintenance.None;
        }

        var needed = await Database.ReadAsync(
            (connection, _) => Task.FromResult(NeedsMaintenance(connection, null)),
            cancellationToken).ConfigureAwait(false);
        if (!needed)
        {
            return VectorMaintenance.None;
        }

        return await Database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var session = BeginWrite(connection, transaction);
                return Task.FromResult(session.Maintain());
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal static int? ReadDimensions(SqliteConnection connection, SqliteTransaction? transaction) =>
        int.TryParse(Sql.ReadMeta(connection, transaction, IndexStorageSchema.MetaKeys.VectorDimensions), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dims) && dims > 0
            ? dims
            : null;

    internal static bool IsStale(SqliteConnection connection, SqliteTransaction? transaction) =>
        string.Equals(Sql.ReadMeta(connection, transaction, IndexStorageSchema.MetaKeys.VectorIndexState), Stale, StringComparison.Ordinal);

    private static bool NeedsMaintenance(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var dims = ReadDimensions(connection, transaction);
        if (dims is null)
        {
            return false;
        }

        return !Sql.TableExists(connection, transaction, IndexStorageSchema.VectorTable)
            || IsStale(connection, transaction)
            || Sql.ScalarLong(connection, transaction, "SELECT EXISTS (SELECT 1 FROM vec_deleted)") != 0;
    }
}

/// <summary>What <see cref="VectorStorage.MaintainAsync"/> did.</summary>
internal enum VectorMaintenance
{
    /// <summary>Nothing was needed.</summary>
    None,

    /// <summary>Rows of deleted chunks were dropped from <c>chunk_vectors</c>.</summary>
    Drained,

    /// <summary><c>chunk_vectors</c> was created or rebuilt from <c>chunk_embeddings</c>.</summary>
    Rebuilt,
}

/// <summary>Vector writes within one write transaction; prepared commands are reused across rows.</summary>
internal sealed class VectorWriteSession : IDisposable
{
    private readonly VectorStorage _storage;
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly List<SqliteCommand> _commands = [];
    private int? _dimensions;
    private bool _vectorTableExists;
    private bool _markedStale;
    private SqliteCommand? _upsertPlain;
    private SqliteCommand? _insertVec;
    private SqliteCommand? _deleteVec;
    private SqliteCommand? _upsertFloat32;

    public VectorWriteSession(VectorStorage storage, SqliteConnection connection, SqliteTransaction transaction)
    {
        _storage = storage;
        _connection = connection;
        _transaction = transaction;
        _dimensions = VectorStorage.ReadDimensions(connection, transaction);
        _vectorTableExists = Sql.TableExists(connection, transaction, IndexStorageSchema.VectorTable);

        // Rows of chunks deleted by a host without the extension go first, so a new row can never be dropped by an old delete.
        if (Writable)
        {
            DrainDeleted();
        }
    }

    /// <summary>Gets a value indicating whether <c>chunk_vectors</c> exists and this process can write it.</summary>
    private bool Writable => _vectorTableExists && _storage.ExtensionLoaded;

    /// <summary>Stores (or replaces) the int8 vector of a chunk.</summary>
    /// <param name="chunkId">The chunk; it must exist.</param>
    /// <param name="vector">The vector.</param>
    /// <param name="language">The language of the chunk's file.</param>
    /// <param name="partition">The partition of the chunk's file.</param>
    /// <param name="isNew">Whether the chunk was inserted in this transaction (so it has no vector row yet).</param>
    public void Upsert(long chunkId, ReadOnlySpan<sbyte> vector, string? language, string? partition, bool isNew)
    {
        EnsureDimensions(vector.Length);
        var blob = Sql.ToBlob(vector);

        _upsertPlain ??= Prepare(
            "INSERT INTO chunk_embeddings (chunk_id, language, partition, embedding) VALUES ($id, $language, $partition, $embedding) "
            + "ON CONFLICT (chunk_id) DO UPDATE SET language = excluded.language, partition = excluded.partition, embedding = excluded.embedding",
            "$id",
            "$language",
            "$partition",
            "$embedding");
        Run(_upsertPlain, chunkId, language ?? string.Empty, partition ?? string.Empty, blob);

        if (Writable)
        {
            if (!isNew)
            {
                DeleteVectorRow(chunkId);
            }

            _insertVec ??= Prepare(
                $"INSERT INTO {IndexStorageSchema.VectorTable} (chunk_id, embedding, language, partition) VALUES ($id, vec_int8($embedding), $language, $partition)",
                "$id",
                "$embedding",
                "$language",
                "$partition");
            Run(_insertVec, chunkId, blob, language ?? string.Empty, partition ?? string.Empty);
        }
        else
        {
            MarkStaleIfNeeded();
        }
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
        if (_vectorTableExists)
        {
            if (Writable)
            {
                foreach (var id in ChunkIdsWithVectors(fileId))
                {
                    DeleteVectorRow(id);
                }
            }
            else
            {
                Sql.Execute(
                    _connection,
                    _transaction,
                    "INSERT OR IGNORE INTO vec_deleted (chunk_id) SELECT e.chunk_id FROM chunk_embeddings e JOIN chunks c ON c.id = e.chunk_id WHERE c.file_id = $file",
                    ("$file", fileId));
            }
        }

        Sql.Execute(_connection, _transaction, "DELETE FROM vec_float32 WHERE chunk_id IN (SELECT id FROM chunks WHERE file_id = $file)", ("$file", fileId));
        return Sql.Execute(_connection, _transaction, "DELETE FROM chunk_embeddings WHERE chunk_id IN (SELECT id FROM chunks WHERE file_id = $file)", ("$file", fileId));
    }

    /// <summary>
    /// Drops the <c>chunk_vectors</c> rows of chunks deleted since the last drain (the delete trigger records them), or
    /// just forgets them when there is no <c>chunk_vectors</c> table. Leaves them for later when the table exists and
    /// this process cannot write it.
    /// </summary>
    /// <returns>The number of ids handled.</returns>
    public int DrainDeleted()
    {
        if (!_vectorTableExists)
        {
            return Sql.Execute(_connection, _transaction, "DELETE FROM vec_deleted");
        }

        if (!Writable)
        {
            return 0;
        }

        var ids = new List<long>();
        using (var select = Sql.Command(_connection, _transaction, "SELECT chunk_id FROM vec_deleted"))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in ids)
        {
            DeleteVectorRow(id);
        }

        if (ids.Count > 0)
        {
            Sql.Execute(_connection, _transaction, "DELETE FROM vec_deleted");
        }

        return ids.Count;
    }

    /// <summary>Creates <c>chunk_vectors</c> when missing and the dimension is known, and rebuilds it when stale.</summary>
    /// <returns>What was done.</returns>
    public VectorMaintenance Maintain()
    {
        if (!_storage.ExtensionLoaded || _dimensions is null)
        {
            return VectorMaintenance.None;
        }

        if (!_vectorTableExists || VectorStorage.IsStale(_connection, _transaction))
        {
            Rebuild();
            return VectorMaintenance.Rebuilt;
        }

        return DrainDeleted() > 0 ? VectorMaintenance.Drained : VectorMaintenance.None;
    }

    public void Dispose()
    {
        foreach (var command in _commands)
        {
            command.Dispose();
        }
    }

    private void Rebuild()
    {
        Sql.Execute(_connection, _transaction, $"DROP TABLE IF EXISTS {IndexStorageSchema.VectorTable}");
        CreateVectorTable(_dimensions!.Value);
        Sql.Execute(
            _connection,
            _transaction,
            $"INSERT INTO {IndexStorageSchema.VectorTable} (chunk_id, embedding, language, partition) SELECT chunk_id, vec_int8(embedding), language, partition FROM chunk_embeddings");
        Sql.Execute(_connection, _transaction, "DELETE FROM vec_deleted");
        Sql.WriteMeta(_connection, _transaction, IndexStorageSchema.MetaKeys.VectorIndexState, VectorStorage.Synced);
        _markedStale = false;
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

            if (!_vectorTableExists && _storage.ExtensionLoaded)
            {
                // The dimension was recorded (by the embedding step, or by a host without the extension): build the table now.
                Rebuild();
            }

            return;
        }

        Sql.WriteMeta(_connection, _transaction, IndexStorageSchema.MetaKeys.VectorDimensions, length.ToString(CultureInfo.InvariantCulture));
        _dimensions = length;
        if (_storage.ExtensionLoaded)
        {
            Rebuild();
        }
    }

    private void CreateVectorTable(int dimensions)
    {
        Sql.Execute(
            _connection,
            _transaction,
            string.Create(
                CultureInfo.InvariantCulture,
                $"CREATE VIRTUAL TABLE {IndexStorageSchema.VectorTable} USING vec0 (chunk_id integer primary key, embedding int8[{dimensions}] distance_metric=cosine, language text, partition text)"));
        _vectorTableExists = true;
    }

    private void MarkStaleIfNeeded()
    {
        if (_vectorTableExists && !_markedStale)
        {
            Sql.WriteMeta(_connection, _transaction, IndexStorageSchema.MetaKeys.VectorIndexState, VectorStorage.Stale);
            _markedStale = true;
        }
    }

    private void DeleteVectorRow(long chunkId)
    {
        _deleteVec ??= Prepare($"DELETE FROM {IndexStorageSchema.VectorTable} WHERE chunk_id = $id", "$id");
        Run(_deleteVec, chunkId);
    }

    private List<long> ChunkIdsWithVectors(long fileId)
    {
        var ids = new List<long>();
        using var command = Sql.Command(
            _connection,
            _transaction,
            "SELECT e.chunk_id FROM chunk_embeddings e JOIN chunks c ON c.id = e.chunk_id WHERE c.file_id = $file",
            ("$file", fileId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
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

    private static void Run(SqliteCommand command, params object[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters[i].Value = values[i];
        }

        command.ExecuteNonQuery();
    }
}
