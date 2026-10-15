using System.Globalization;
using Pillion.ContextEngine.Symbols;
using Pillion.LocalStore.Index;

namespace Pillion.ContextEngine.Retrieval;

/// <summary>A file of an index as retrieval needs it: the <c>files</c> row without the hash columns.</summary>
/// <param name="Id">The <c>files.id</c>.</param>
/// <param name="Path">The path relative to the repository root, with <c>/</c> separators.</param>
/// <param name="Language">The language.</param>
/// <param name="Partition">The partition.</param>
public sealed record RetrievalFile(long Id, string Path, string Language, string Partition);

/// <summary>
/// One open index as retrieval sees it: its chunk, vector and symbol storage, the files table, the embedding model its
/// vectors came from and, when the host has one in memory, its symbol graph. The host adapts its open indexes to this.
/// </summary>
public interface IRetrievalIndex
{
    /// <summary>Gets the index id (32 hex digits); it prefixes the opaque chunk ids.</summary>
    string IndexId { get; }

    /// <summary>Gets the repository root the index covers (a full path).</summary>
    string RootPath { get; }

    /// <summary>Gets the embedding model id the vectors were made with (index <c>meta.embedding_model</c>), or <see langword="null"/> when unknown.</summary>
    string? EmbeddingModelId { get; }

    /// <summary>Gets the vector size of the index (<c>meta.vector_dimensions</c>), or <see langword="null"/> when nothing is embedded yet.</summary>
    int? VectorDimensions { get; }

    /// <summary>Gets the chunks and lexical search.</summary>
    IChunkRepository Chunks { get; }

    /// <summary>Gets the vector search.</summary>
    IVectorIndex Vectors { get; }

    /// <summary>Gets the stored symbols; used for exact symbol hits when <see cref="Graph"/> is <see langword="null"/>.</summary>
    ISymbolRepository Symbols { get; }

    /// <summary>Gets the in-memory symbol graph of the index, or <see langword="null"/> when the host has none.</summary>
    SymbolGraph? Graph { get; }

    /// <summary>Gets files by id.</summary>
    /// <param name="fileIds">The ids.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The files that exist, by id.</returns>
    Task<IReadOnlyDictionary<long, RetrievalFile>> GetFilesAsync(IReadOnlyCollection<long> fileIds, CancellationToken cancellationToken);

    /// <summary>Finds files by relative path, without regard to case.</summary>
    /// <param name="paths">Paths relative to the root, with <c>/</c> separators.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The files that exist, keyed by the path as given (case-insensitively).</returns>
    Task<IReadOnlyDictionary<string, RetrievalFile>> FindFilesAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken);
}

/// <summary>A query vector: the embedded question, unit-normalised and quantised to int8 like the index's vectors.</summary>
/// <param name="Vector">The int8 vector.</param>
/// <param name="ModelId">The model that produced it, as the embedder reports it.</param>
public sealed record QueryEmbedding(sbyte[] Vector, string ModelId);

/// <summary>Embeds a question with the model an index was built with (the host implements it over the Model Gateway).</summary>
public interface IQueryEmbedder
{
    /// <summary>Embeds one query.</summary>
    /// <param name="text">The query text.</param>
    /// <param name="modelId">The index's embedding model id, or <see langword="null"/> to let routing choose.</param>
    /// <param name="dimensions">The index's vector size, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The vector.</returns>
    Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dimensions, CancellationToken cancellationToken);
}

/// <summary>A candidate offered to an <see cref="IReranker"/>.</summary>
/// <param name="Key">An opaque key the reranker hands back.</param>
/// <param name="Text">The candidate's chunk text (breadcrumb, line break, text).</param>
/// <param name="Score">The score so far (fused plus boosts).</param>
public sealed record RerankCandidate(string Key, string Text, double Score);

/// <summary>
/// Reorders the top fused candidates (design section 7, Fuse: an optional cross-encoder reorders the top 30). Only the
/// order-keeping <see cref="NoOpReranker"/> ships: the cross-encoder waits on the licence review in section 18.
/// </summary>
public interface IReranker
{
    /// <summary>Gets the reranker's name for logs.</summary>
    string Name { get; }

    /// <summary>Reranks candidates.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="candidates">The candidates, best first.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The same candidates (keys) in the new order, each with its new score.</returns>
    Task<IReadOnlyList<RerankCandidate>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken cancellationToken);
}

/// <summary>The reranker that keeps the fused order; the only one in this version.</summary>
public sealed class NoOpReranker : IReranker
{
    /// <summary>Gets the shared instance.</summary>
    public static NoOpReranker Instance { get; } = new();

    /// <inheritdoc/>
    public string Name => "none";

    /// <inheritdoc/>
    public Task<IReadOnlyList<RerankCandidate>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken cancellationToken) =>
        Task.FromResult(candidates);
}

/// <summary>
/// The opaque chunk id of the contract (<c>SearchResult.ChunkId</c>, <c>Citation.ChunkId</c>): the index id and the chunk's
/// row id, <c>&lt;index-id&gt;:&lt;row-id&gt;</c>. Clients treat it as opaque; the host parses it back.
/// </summary>
public static class RetrievalChunkIds
{
    /// <summary>Formats a chunk id.</summary>
    /// <param name="indexId">The index id.</param>
    /// <param name="chunkId">The chunk's row id.</param>
    /// <returns>The opaque id.</returns>
    public static string Format(string indexId, long chunkId) =>
        indexId + ":" + chunkId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses a chunk id made by <see cref="Format"/>.</summary>
    /// <param name="value">The opaque id.</param>
    /// <param name="indexId">Receives the index id.</param>
    /// <param name="chunkId">Receives the row id.</param>
    /// <returns><see langword="true"/> when the value is well formed.</returns>
    public static bool TryParse(string? value, out string indexId, out long chunkId)
    {
        indexId = string.Empty;
        chunkId = 0;
        var colon = value?.LastIndexOf(':') ?? -1;
        if (colon <= 0 || !long.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out chunkId) || chunkId <= 0)
        {
            chunkId = 0;
            return false;
        }

        indexId = value![..colon];
        return true;
    }
}
