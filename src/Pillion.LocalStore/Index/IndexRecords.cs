namespace Pillion.LocalStore.Index;

/// <summary>
/// A chunk to store for a file (the storage row; the chunkers' own <c>Chunk</c> type maps onto it). Lines are 1-based
/// and inclusive.
/// </summary>
/// <param name="StartLine">The first line of the chunk.</param>
/// <param name="EndLine">The last line of the chunk.</param>
/// <param name="Kind">What the chunk is, for example <c>method</c>, <c>type-header</c> or <c>window</c>.</param>
/// <param name="Breadcrumb">The breadcrumb <c>path &gt; namespace &gt; type &gt; member</c>; indexed with the text.</param>
/// <param name="Text">The chunk text as it is in the file.</param>
/// <param name="TokenCount">Its size in the embedding model's tokens.</param>
public sealed record NewChunk(int StartLine, int EndLine, string Kind, string Breadcrumb, string Text, int TokenCount)
{
    /// <summary>Gets the int8 embedding, or empty when the chunk is not embedded yet (see <see cref="IChunkRepository.UpsertEmbeddingsAsync"/>).</summary>
    public ReadOnlyMemory<sbyte> Embedding { get; init; }

    /// <summary>Gets the float32 embedding, or empty. <b>Development only</b> (int8 recall-loss measurement); dropped before release.</summary>
    public ReadOnlyMemory<float> EmbeddingFloat32 { get; init; }
}

/// <summary>The chunks of one file, for a batch write.</summary>
/// <param name="FileId">The <c>files.id</c> of the file; it must exist.</param>
/// <param name="Chunks">Its chunks, replacing the ones stored.</param>
public sealed record FileChunks(long FileId, IReadOnlyList<NewChunk> Chunks);

/// <summary>A stored chunk.</summary>
/// <param name="Id">The chunk id (also its FTS5 rowid and its vector key).</param>
/// <param name="FileId">The file.</param>
/// <param name="StartLine">The first line.</param>
/// <param name="EndLine">The last line.</param>
/// <param name="Kind">The chunk kind.</param>
/// <param name="Breadcrumb">The breadcrumb.</param>
/// <param name="Text">The text.</param>
/// <param name="TokenCount">The token count.</param>
public sealed record ChunkRecord(long Id, long FileId, int StartLine, int EndLine, string Kind, string Breadcrumb, string Text, int TokenCount);

/// <summary>An embedding for a chunk that is already stored.</summary>
/// <param name="ChunkId">The chunk.</param>
/// <param name="Vector">The unit-normalised, int8-quantised vector.</param>
public sealed record ChunkEmbedding(long ChunkId, ReadOnlyMemory<sbyte> Vector)
{
    /// <summary>Gets the float32 vector, or empty. <b>Development only</b>; dropped before release.</summary>
    public ReadOnlyMemory<float> VectorFloat32 { get; init; }
}

/// <summary>One file in the per-file listing of the index.</summary>
/// <param name="FileId">The file id.</param>
/// <param name="Path">The file path as <c>files.path</c> holds it.</param>
/// <param name="Chunks">Its chunk count.</param>
/// <param name="Tokens">The sum of its chunks' token counts.</param>
/// <param name="Embedded">How many of its chunks have a vector.</param>
public sealed record FileChunkSummary(long FileId, string Path, int Chunks, long Tokens, int Embedded);

/// <summary>The size of the index database without a <c>VACUUM</c>: SQLite's own page accounting.</summary>
/// <param name="PageCount">The pages in the database (including committed pages still in the WAL).</param>
/// <param name="PageSize">The page size in bytes.</param>
/// <param name="FreePages">Pages on the free list (reusable without growing the file).</param>
/// <param name="WalBytes">The size of the <c>-wal</c> file on disk.</param>
public sealed record IndexSize(long PageCount, long PageSize, long FreePages, long WalBytes)
{
    /// <summary>Gets the database size in bytes (<c>page_count * page_size</c>).</summary>
    public long Bytes => PageCount * PageSize;

    /// <summary>Gets the bytes on the free list.</summary>
    public long FreeBytes => FreePages * PageSize;
}

/// <summary>
/// Restricts a search to some partitions (project or top-level folder, design section 7) and languages. A null or empty
/// list does not restrict; several values match any of them.
/// </summary>
public sealed record ChunkFilter
{
    /// <summary>Gets a filter that matches everything.</summary>
    public static ChunkFilter None { get; } = new();

    /// <summary>Gets the partitions to keep, or <see langword="null"/> for all.</summary>
    public IReadOnlyList<string>? Partitions { get; init; }

    /// <summary>Gets the languages to keep, or <see langword="null"/> for all.</summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>Gets a value indicating whether the filter restricts anything.</summary>
    public bool IsEmpty => (Partitions is null || Partitions.Count == 0) && (Languages is null || Languages.Count == 0);
}

/// <summary>A lexical search result.</summary>
/// <param name="ChunkId">The chunk.</param>
/// <param name="Score">The negated FTS5 <c>bm25()</c> with the column weights: higher is a better match.</param>
public readonly record struct LexicalHit(long ChunkId, double Score);

/// <summary>A vector search result.</summary>
/// <param name="ChunkId">The chunk.</param>
/// <param name="Distance">The cosine distance (1 minus the cosine similarity, 0 to 2): lower is closer.</param>
public readonly record struct VectorHit(long ChunkId, double Distance);

/// <summary>What a vector carries besides the vector, for filtering.</summary>
/// <param name="Language">The language of the chunk's file.</param>
/// <param name="Partition">The partition of the chunk's file.</param>
public sealed record VectorMetadata(string? Language, string? Partition);

/// <summary>A syntax-derived symbol to store (work item 2.5); marked approximate by nature.</summary>
/// <param name="Name">The symbol name.</param>
/// <param name="Kind">The symbol kind, for example <c>class</c> or <c>method</c>.</param>
/// <param name="Container">The containing symbol's qualified name, if any.</param>
/// <param name="StartLine">The first line.</param>
/// <param name="EndLine">The last line.</param>
/// <param name="Signature">The signature text, if any.</param>
public sealed record NewSymbol(string Name, string Kind, string? Container, int StartLine, int EndLine, string? Signature);

/// <summary>A stored symbol.</summary>
/// <param name="Id">The symbol id.</param>
/// <param name="FileId">The file.</param>
/// <param name="Name">The name.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Container">The container.</param>
/// <param name="StartLine">The first line.</param>
/// <param name="EndLine">The last line.</param>
/// <param name="Signature">The signature.</param>
public sealed record SymbolRecord(long Id, long FileId, string Name, string Kind, string? Container, int StartLine, int EndLine, string? Signature);

/// <summary>A name-based reference edge (approximate).</summary>
/// <param name="FromSymbolId">The referencing symbol, or <see langword="null"/> at file scope.</param>
/// <param name="ToSymbolId">The referenced symbol.</param>
/// <param name="FileId">The file the reference is in.</param>
/// <param name="Line">The line of the reference.</param>
/// <param name="Kind">The reference kind, for example <c>call</c> or <c>type</c>.</param>
public sealed record SymbolReference(long? FromSymbolId, long ToSymbolId, long FileId, int Line, string Kind);
