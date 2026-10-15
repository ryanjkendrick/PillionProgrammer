namespace Pillion.LocalStore.Index;

/// <summary>
/// The chunks of an index with their FTS5 and vector rows kept in step, lexical search over them, and the size figures
/// of the Index window. Writes for a file replace all of its chunks in one transaction.
/// </summary>
public interface IChunkRepository
{
    /// <summary>
    /// Replaces the chunks of one file in one transaction: its old chunks, their FTS5 rows and vectors go, the new chunks
    /// and their FTS5 rows (text and split identifiers) and any vectors given come in.
    /// </summary>
    /// <param name="fileId">The file (<c>files.id</c>); it must exist.</param>
    /// <param name="chunks">The new chunks; empty removes them all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new chunk ids, in the order of <paramref name="chunks"/>.</returns>
    /// <exception cref="KeyNotFoundException">The file does not exist.</exception>
    Task<IReadOnlyList<long>> ReplaceFileChunksAsync(long fileId, IReadOnlyList<NewChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>Replaces the chunks of several files in one transaction (an indexing batch); see <see cref="ReplaceFileChunksAsync"/>.</summary>
    /// <param name="files">The files and their chunks.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new chunk ids of each file, in order.</returns>
    /// <exception cref="KeyNotFoundException">A file does not exist; nothing is written.</exception>
    Task<IReadOnlyList<IReadOnlyList<long>>> ReplaceFilesChunksAsync(IReadOnlyList<FileChunks> files, CancellationToken cancellationToken = default);

    /// <summary>Deletes every chunk of a file with its FTS5 rows and vectors.</summary>
    /// <param name="fileId">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of chunks deleted.</returns>
    Task<int> DeleteFileChunksAsync(long fileId, CancellationToken cancellationToken = default);

    /// <summary>Stores vectors for chunks already stored (embedding runs after chunking); the language and partition come from the chunk's file.</summary>
    /// <param name="embeddings">The vectors.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number stored; embeddings of chunks that no longer exist are skipped.</returns>
    Task<int> UpsertEmbeddingsAsync(IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken cancellationToken = default);

    /// <summary>Gets a chunk.</summary>
    /// <param name="chunkId">The chunk id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The chunk, or <see langword="null"/>.</returns>
    Task<ChunkRecord?> GetChunkAsync(long chunkId, CancellationToken cancellationToken = default);

    /// <summary>Gets several chunks (for example the hits of a search).</summary>
    /// <param name="chunkIds">The chunk ids.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The chunks that exist, in the order of <paramref name="chunkIds"/>.</returns>
    Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken cancellationToken = default);

    /// <summary>Gets the chunks of a file, by start line.</summary>
    /// <param name="fileId">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The chunks.</returns>
    Task<IReadOnlyList<ChunkRecord>> GetChunksByFileAsync(long fileId, CancellationToken cancellationToken = default);

    /// <summary>Counts the chunks of the index.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The chunk count.</returns>
    Task<long> CountChunksAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the files that have chunks, with their chunk, token and vector counts, by path.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The files.</returns>
    Task<IReadOnlyList<FileChunkSummary>> ListFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the size of the database from SQLite's page accounting (no <c>VACUUM</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The size.</returns>
    Task<IndexSize> GetSizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lexical search with BM25 (column weights from <see cref="IndexStorageOptions"/>). The query is split into tokens
    /// and each is matched whole and identifier-split, so <c>getOrderTotal</c> matches <c>order total</c> and the reverse;
    /// the last token matches as a prefix. See <see cref="IdentifierSplitter.BuildMatchQuery"/>.
    /// </summary>
    /// <param name="query">The query text.</param>
    /// <param name="k">How many to return.</param>
    /// <param name="filter">Partitions and languages to keep (from the chunk's file), or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The best matches, best first; empty for a query without tokens.</returns>
    Task<IReadOnlyList<LexicalHit>> SearchLexicalAsync(string query, int k, ChunkFilter? filter = null, CancellationToken cancellationToken = default);
}
