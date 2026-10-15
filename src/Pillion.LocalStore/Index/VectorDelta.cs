namespace Pillion.LocalStore.Index;

/// <summary>A vector stored by a transaction, with the filter columns of its chunk's file.</summary>
/// <param name="ChunkId">The chunk.</param>
/// <param name="Vector">The int8 vector.</param>
/// <param name="Language">The language of the chunk's file.</param>
/// <param name="Partition">The partition of the chunk's file.</param>
internal sealed record AddedVector(long ChunkId, ReadOnlyMemory<sbyte> Vector, string? Language, string? Partition);

/// <summary>
/// What one write transaction did to <c>chunk_embeddings</c> (work item 2.10): the <c>vec_generation</c> value before and
/// after, the chunk ids whose vectors went, and the vectors stored. Applied to the in-memory copy after the commit
/// (<see cref="VectorStorage.ApplyDelta"/>) so a saved file is searchable by vector without reloading every vector.
/// </summary>
internal sealed class VectorDelta
{
    /// <summary>Gets the generation before the transaction's first vector change.</summary>
    public long Before { get; init; }

    /// <summary>Gets the generation at the end of the transaction.</summary>
    public long After { get; init; }

    /// <summary>Gets the chunk ids whose vectors were deleted.</summary>
    public IReadOnlyCollection<long> Removed { get; init; } = [];

    /// <summary>Gets the vectors stored (inserted or replaced).</summary>
    public IReadOnlyList<AddedVector> Added { get; init; } = [];
}
