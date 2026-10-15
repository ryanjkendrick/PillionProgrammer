using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Index;

/// <summary>
/// Writes to an index database inside one write transaction (see <see cref="IndexStorage.WriteAsync{T}"/>): the indexing
/// pipeline stores a file's <c>files</c> row (through the connection and transaction exposed here), its chunks with their
/// FTS5 rows and vectors, and its symbols together, so a file is either fully stored or not at all. The same rules as
/// <see cref="IChunkRepository.ReplaceFileChunksAsync"/>, <see cref="ISymbolRepository.ReplaceFileSymbolsAsync"/> and
/// <see cref="ISymbolRepository.ReplaceFileReferencesAsync"/> apply. Valid only inside the delegate it is passed to.
/// </summary>
public sealed class IndexWriteSession : IDisposable
{
    private ChunkRepository.ChunkWriter? _chunks;
    private VectorWriteSession? _vectors;
    private SqliteCommand? _vectorIdsOfFile;
    private bool _updateVectorCache;
    private long? _generationBefore;
    private HashSet<long>? _removed;
    private List<AddedVector>? _added;
    private int _changes;

    internal IndexWriteSession(SqliteConnection connection, SqliteTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    /// <summary>Gets the writer connection, for other tables of the same database (the <c>files</c> table of repository discovery).</summary>
    public SqliteConnection Connection { get; }

    /// <summary>Gets the open write transaction; commands on <see cref="Connection"/> must use it.</summary>
    public SqliteTransaction Transaction { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the vectors this session stores and deletes are applied to the in-memory copy
    /// of the vectors after the commit (work item 2.10), instead of the copy being reloaded whole by the next search or
    /// <see cref="IndexStorage.WarmVectorCacheAsync"/>. Set it before the first write: only the writes made through this
    /// session afterwards (<see cref="ReplaceFileChunks"/>, <see cref="DeleteFileChunks"/>, <see cref="UpsertEmbeddings"/>)
    /// are tracked, and if anything else changed the vectors meanwhile the copy is left to reload. The incremental indexer
    /// sets it for saved and watched files; a full run leaves it off (applying thousands of small changes would cost more
    /// than one reload).
    /// </summary>
    public bool UpdateVectorCache
    {
        get => _updateVectorCache;
        set
        {
            if (value && !_updateVectorCache)
            {
                _generationBefore ??= VectorCache.ReadGeneration(Connection, Transaction);
                _removed ??= [];
                _added ??= [];
            }

            _updateVectorCache = value;
        }
    }

    /// <summary>Replaces the chunks of one file (with their FTS5 rows and any vectors given); see <see cref="IChunkRepository.ReplaceFileChunksAsync"/>.</summary>
    /// <param name="fileId">The file (<c>files.id</c>); it must exist, possibly inserted earlier in this transaction.</param>
    /// <param name="chunks">The new chunks; empty removes them all.</param>
    /// <returns>The new chunk ids, in order.</returns>
    /// <exception cref="KeyNotFoundException">The file does not exist.</exception>
    public IReadOnlyList<long> ReplaceFileChunks(long fileId, IReadOnlyList<NewChunk> chunks)
    {
        ChunkRepository.Validate(chunks);
        _chunks ??= new ChunkRepository.ChunkWriter(Connection, Transaction);
        _vectors ??= VectorStorage.BeginWrite(Connection, Transaction);
        TrackRemovedVectorsOf(fileId);
        return ChunkRepository.ReplaceChunks(_chunks, _vectors, fileId, chunks, _updateVectorCache ? TrackAdded : null);
    }

    /// <summary>
    /// Deletes the chunks of one file (their FTS5 rows by trigger, their vectors by cascade), leaving the <c>files</c> row;
    /// the incremental indexer calls it before deleting a file's row so the in-memory vector copy can drop those vectors.
    /// </summary>
    /// <param name="fileId">The file.</param>
    /// <returns>The number of chunks deleted.</returns>
    public int DeleteFileChunks(long fileId)
    {
        TrackRemovedVectorsOf(fileId);
        return Sql.Execute(Connection, Transaction, "DELETE FROM chunks WHERE file_id = $file", ("$file", fileId));
    }

    /// <summary>Stores vectors for chunks already stored; see <see cref="IChunkRepository.UpsertEmbeddingsAsync"/>.</summary>
    /// <param name="embeddings">The vectors.</param>
    /// <param name="cancellationToken">Cancels between vectors.</param>
    /// <returns>The number stored; vectors of chunks that no longer exist are skipped.</returns>
    public int UpsertEmbeddings(IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        _vectors ??= VectorStorage.BeginWrite(Connection, Transaction);
        return ChunkRepository.UpsertEmbeddings(Connection, Transaction, _vectors, embeddings, _updateVectorCache ? TrackAdded : null, cancellationToken);
    }

    /// <summary>Replaces the symbols of one file, upserting those with stable ids; see <see cref="ISymbolRepository.ReplaceFileSymbolsAsync"/>.</summary>
    /// <param name="fileId">The file; it must exist.</param>
    /// <param name="symbols">The new symbols; stable ids must be unique within the list.</param>
    /// <param name="cancellationToken">Cancels between symbols.</param>
    /// <returns>The symbol ids, in order.</returns>
    public IReadOnlyList<long> ReplaceFileSymbols(long fileId, IReadOnlyList<NewSymbol> symbols, CancellationToken cancellationToken = default) =>
        SymbolRepository.ReplaceSymbols(Connection, Transaction, fileId, symbols, cancellationToken);

    /// <summary>Replaces the references found in one file; see <see cref="ISymbolRepository.ReplaceFileReferencesAsync"/>.</summary>
    /// <param name="fileId">The file the references are in.</param>
    /// <param name="references">The references; every symbol they name must exist.</param>
    /// <param name="cancellationToken">Cancels between references.</param>
    public void ReplaceFileReferences(long fileId, IReadOnlyList<SymbolReference> references, CancellationToken cancellationToken = default) =>
        SymbolRepository.ReplaceReferences(Connection, Transaction, fileId, references, cancellationToken);

    /// <summary>Writes a <c>meta</c> row (insert or replace).</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    public void WriteMeta(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        Sql.WriteMeta(Connection, Transaction, key, value);
    }

    /// <summary>Reads a <c>meta</c> row.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    public string? ReadMeta(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Sql.ReadMeta(Connection, Transaction, key);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _chunks?.Dispose();
        _vectors?.Dispose();
        _vectorIdsOfFile?.Dispose();
    }

    /// <summary>
    /// Ends tracking inside the transaction: the change to apply to the in-memory copy after the commit, or
    /// <see langword="null"/> when nothing was tracked, nothing changed, or the generation moved by other than the tracked
    /// writes (something else changed the vectors, so only a reload is safe).
    /// </summary>
    internal VectorDelta? CompleteVectorDelta()
    {
        if (_generationBefore is not { } before || _changes == 0)
        {
            return null;
        }

        var after = VectorCache.ReadGeneration(Connection, Transaction);
        return after - before != _changes
            ? null
            : new VectorDelta { Before = before, After = after, Removed = _removed!, Added = _added! };
    }

    private void TrackRemovedVectorsOf(long fileId)
    {
        if (!_updateVectorCache)
        {
            return;
        }

        _vectorIdsOfFile ??= Sql.Command(
            Connection,
            Transaction,
            "SELECT e.chunk_id FROM chunk_embeddings e JOIN chunks c ON c.id = e.chunk_id WHERE c.file_id = $file",
            ("$file", 0L));
        _vectorIdsOfFile.Parameters[0].Value = fileId;
        using var reader = _vectorIdsOfFile.ExecuteReader();
        while (reader.Read())
        {
            // Each deleted row bumps the generation once (the cascade fires the delete trigger).
            if (_removed!.Add(reader.GetInt64(0)))
            {
                _changes++;
            }
        }
    }

    private void TrackAdded(long chunkId, ReadOnlyMemory<sbyte> vector, string? language, string? partition)
    {
        // Each insert or upsert bumps the generation once. The caller's buffer may be reused: keep a copy.
        _added!.Add(new AddedVector(chunkId, vector.ToArray(), language, partition));
        _changes++;
    }
}
