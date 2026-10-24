namespace Pillion.ContextEngine.Retrieval;

/// <summary>How a search finds its candidates (design section 7, Hybrid retrieval); mirrors the contract's <c>SearchMode</c>.</summary>
public enum RetrievalSearchMode
{
    /// <summary>Lexical (BM25) and vector candidates and exact symbol hits, fused with Reciprocal Rank Fusion.</summary>
    Hybrid,

    /// <summary>The lexical (BM25) leg alone.</summary>
    Lexical,

    /// <summary>The vector leg alone.</summary>
    Vector,

    /// <summary>The exact symbol-name leg alone.</summary>
    Symbol,

    /// <summary>
    /// The lexical (BM25) and exact symbol-name legs fused, without the vector leg: no query embedding, so it fits a budget
    /// shorter than one embedding on the CPU (autocomplete snippets, work item 2.12). Not on the contract.
    /// </summary>
    LexicalAndSymbol,
}

/// <summary>A search as the retriever receives it.</summary>
public sealed record RetrievalQuery
{
    /// <summary>Gets the natural-language or keyword query.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the most hits to return; 0 or less uses <see cref="RetrievalOptions.DefaultMaxResults"/>.</summary>
    public int MaxResults { get; init; }

    /// <summary>Gets how candidates are found.</summary>
    public RetrievalSearchMode Mode { get; init; }

    /// <summary>Gets the partitions to keep; empty keeps all.</summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>Gets the languages to keep; empty keeps all.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>Gets the most hits from one file; 0 or less uses <see cref="RetrievalOptions.DefaultMaxPerFile"/>.</summary>
    public int MaxPerFile { get; init; }

    /// <summary>Gets a value indicating whether each hit carries the full text of its expanded span.</summary>
    public bool IncludeText { get; init; }

    /// <summary>Gets the full paths of the open and recently edited files, most recent first, which get the focus boost.</summary>
    public IReadOnlyList<string> FocusFiles { get; init; } = [];

    /// <summary>Gets the time budget of this search, or <see langword="null"/> for <see cref="RetrievalOptions.TimeBudget"/>.</summary>
    public TimeSpan? TimeBudget { get; init; }
}

/// <summary>Tuning for <see cref="HybridRetriever"/>. The defaults follow design section 7 and the budgets of section 13.</summary>
public sealed record RetrievalOptions
{
    /// <summary>Gets the default options.</summary>
    public static RetrievalOptions Default { get; } = new();

    /// <summary>Gets the candidates taken from the lexical leg (top 50 by BM25).</summary>
    public int LexicalCandidates { get; init; } = 50;

    /// <summary>Gets the candidates taken from the vector leg (top 50 by cosine similarity).</summary>
    public int VectorCandidates { get; init; } = 50;

    /// <summary>Gets the most exact symbol-name hits taken.</summary>
    public int SymbolCandidates { get; init; } = 20;

    /// <summary>Gets the Reciprocal Rank Fusion constant <c>k</c>: a hit at rank <c>r</c> of a list scores <c>1 / (k + r)</c>.</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>
    /// Gets the weight of the focus boost: a candidate in the <c>n</c>-th focus file (1-based, most recent first) gains
    /// <c>FocusWeight / (k + n)</c>, as if it were ranked <c>n</c> in one more list. The boost only reorders candidates the
    /// legs found; it never adds chunks of its own.
    /// </summary>
    public double FocusWeight { get; init; } = 1.0;

    /// <summary>Gets the hits returned when the query does not say (the contract's <c>MaxResults</c> of 0).</summary>
    public int DefaultMaxResults { get; init; } = 10;

    /// <summary>Gets the largest <c>MaxResults</c> honoured.</summary>
    public int MaxResultsLimit { get; init; } = 100;

    /// <summary>Gets the most hits from one file when the query does not say (the contract's <c>MaxPerFile</c> of 0).</summary>
    public int DefaultMaxPerFile { get; init; } = 3;

    /// <summary>
    /// Gets the most embedding-model tokens one expanded hit may hold: the hit grows to its full member (every part of a
    /// split unit) and then to one neighbouring chunk on each side while the total stays within this budget. The hit's own
    /// chunk is always kept.
    /// </summary>
    public int MaxExpandedTokens { get; init; } = 1024;

    /// <summary>Gets the time budget of a search when the query does not set one (design section 13: semantic search p95 300 ms).</summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Gets the share of the time budget the candidate legs may use; the rest is kept for fusion and expansion. A leg still
    /// running when it runs out is abandoned and the response is marked degraded.
    /// </summary>
    public double CandidateBudgetShare { get; init; } = 0.8;

    /// <summary>Gets how long a query vector is reused for the same text and model.</summary>
    public TimeSpan QueryVectorCacheTime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets how many query vectors are kept.</summary>
    public int QueryVectorCacheSize { get; init; } = 64;

    /// <summary>Gets the most characters of a hit's snippet.</summary>
    public int MaxSnippetChars { get; init; } = 600;
}

/// <summary>One hit of a search: an expanded span of one file with the ranks it had in each list.</summary>
public sealed record RetrievalHit
{
    /// <summary>Gets the index the hit came from.</summary>
    public required string IndexId { get; init; }

    /// <summary>Gets the hit's chunk (the one the legs found; the span may hold its neighbours too).</summary>
    public required long ChunkId { get; init; }

    /// <summary>Gets the opaque chunk id of the contract: <see cref="RetrievalChunkIds.Format"/> of the index id and <see cref="ChunkId"/>.</summary>
    public string QualifiedChunkId => RetrievalChunkIds.Format(IndexId, ChunkId);

    /// <summary>Gets the full path of the file.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the path of the file relative to its repository root, with <c>/</c> separators.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Gets the 1-based first line of the expanded span.</summary>
    public required int StartLine { get; init; }

    /// <summary>Gets the 1-based last line of the expanded span (inclusive).</summary>
    public required int EndLine { get; init; }

    /// <summary>Gets the hit chunk's breadcrumb.</summary>
    public required string Breadcrumb { get; init; }

    /// <summary>Gets the hit chunk's kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the file's partition.</summary>
    public string Partition { get; init; } = string.Empty;

    /// <summary>Gets the file's language.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Gets the hit chunk's text, cut to <see cref="RetrievalOptions.MaxSnippetChars"/>.</summary>
    public required string Snippet { get; init; }

    /// <summary>Gets the text of the whole expanded span, or <see langword="null"/> unless the query asked for it.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the 1-based rank in the lexical candidates, or 0.</summary>
    public int LexicalRank { get; init; }

    /// <summary>Gets the 1-based rank in the vector candidates, or 0.</summary>
    public int VectorRank { get; init; }

    /// <summary>Gets the 1-based rank in the symbol candidates, or 0.</summary>
    public int SymbolRank { get; init; }

    /// <summary>Gets the 1-based position of the hit's file in the focus files, or 0.</summary>
    public int FocusRank { get; init; }

    /// <summary>Gets the Reciprocal Rank Fusion score before boosts and reranking.</summary>
    public double FusedScore { get; init; }

    /// <summary>Gets the final score the hits are ordered by (the fused score plus the focus boost, or the reranker's).</summary>
    public double Score { get; init; }
}

/// <summary>The answer of <see cref="HybridRetriever.SearchAsync"/>: the hits and where the time went.</summary>
public sealed record RetrievalResponse
{
    /// <summary>Gets an empty response.</summary>
    public static RetrievalResponse Empty { get; } = new();

    /// <summary>Gets the hits, best first.</summary>
    public IReadOnlyList<RetrievalHit> Hits { get; init; } = [];

    /// <summary>Gets the milliseconds of the lexical leg (until it answered or was abandoned).</summary>
    public double LexicalMs { get; init; }

    /// <summary>Gets the milliseconds of the vector leg, embedding the query included.</summary>
    public double VectorMs { get; init; }

    /// <summary>Gets the milliseconds of the symbol leg.</summary>
    public double SymbolMs { get; init; }

    /// <summary>Gets the milliseconds of fusion, expansion, overlap removal and capping.</summary>
    public double FusionMs { get; init; }

    /// <summary>Gets the milliseconds of the whole search.</summary>
    public double TotalMs { get; init; }

    /// <summary>Gets a value indicating whether a leg was cut short by the time budget or failed, so results may be worse than usual.</summary>
    public bool Degraded { get; init; }

    /// <summary>Gets why the response is degraded, one short reason per leg (for logs), or empty.</summary>
    public IReadOnlyList<string> DegradedReasons { get; init; } = [];
}
