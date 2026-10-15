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

    internal IndexWriteSession(SqliteConnection connection, SqliteTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    /// <summary>Gets the writer connection, for other tables of the same database (the <c>files</c> table of repository discovery).</summary>
    public SqliteConnection Connection { get; }

    /// <summary>Gets the open write transaction; commands on <see cref="Connection"/> must use it.</summary>
    public SqliteTransaction Transaction { get; }

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
        return ChunkRepository.ReplaceChunks(_chunks, _vectors, fileId, chunks);
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
    }
}
