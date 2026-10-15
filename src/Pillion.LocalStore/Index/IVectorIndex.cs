namespace Pillion.LocalStore.Index;

/// <summary>
/// Nearest-neighbour search over the chunk vectors of one index (design section 7, Storage). Vectors are unit-normalised
/// and int8-quantised by the embedding step; distances are cosine distances. Brute force is adequate to about 500,000
/// chunks; an HNSW backend for larger monorepos would be another implementation.
/// </summary>
/// <remarks>
/// Every backend stores vectors the same way (the plain <c>chunk_embeddings</c> table), so they differ only in how they
/// search and any of them can read an index another wrote. An HNSW or sqlite-vec backend for indexes beyond the
/// in-memory cap would be another implementation.
/// </remarks>
public interface IVectorIndex
{
    /// <summary>The largest <c>k</c> a search accepts (sqlite-vec's limit, kept so a later backend can honour it).</summary>
    const int MaxK = 4096;

    /// <summary>Gets the backend name for logs and the Index window: <c>in-memory</c> or <c>table-scan</c>.</summary>
    string Backend { get; }

    /// <summary>Stores or replaces the vector of a chunk. The first vector stored fixes the index's dimension.</summary>
    /// <param name="chunkId">The chunk; it must exist.</param>
    /// <param name="vector">The int8 vector.</param>
    /// <param name="metadata">The language and partition, for filters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the vector is committed.</returns>
    /// <exception cref="ArgumentException">The vector's length differs from the index's dimension.</exception>
    Task UpsertAsync(long chunkId, ReadOnlyMemory<sbyte> vector, VectorMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>Removes the vectors of every chunk of a file (the chunks stay).</summary>
    /// <param name="fileId">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of vectors removed.</returns>
    Task<int> DeleteByFileAsync(long fileId, CancellationToken cancellationToken = default);

    /// <summary>Finds the <paramref name="k"/> chunks nearest to a query vector.</summary>
    /// <param name="query">The int8 query vector, embedded and quantised like the index.</param>
    /// <param name="k">How many to return, 1 to <see cref="MaxK"/>.</param>
    /// <param name="filter">Partitions and languages to keep, or <see langword="null"/> for all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The nearest chunks, closest first; empty when the index has no vectors.</returns>
    /// <exception cref="ArgumentException">The query's length differs from the index's dimension.</exception>
    Task<IReadOnlyList<VectorHit>> SearchAsync(ReadOnlyMemory<sbyte> query, int k, ChunkFilter? filter = null, CancellationToken cancellationToken = default);
}
