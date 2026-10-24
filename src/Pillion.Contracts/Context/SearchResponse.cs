using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>How a search finds its candidates (design section 7, Hybrid retrieval). An integer on the wire. Added in contract 1.3.</summary>
public enum SearchMode
{
    /// <summary>BM25 and vector candidates and exact symbol hits, fused with Reciprocal Rank Fusion (the default).</summary>
    Hybrid,

    /// <summary>BM25 over the chunk text, breadcrumbs and split identifiers only.</summary>
    Lexical,

    /// <summary>Vector similarity to the embedded query only.</summary>
    Vector,

    /// <summary>Symbol names only (the symbol table's definitions).</summary>
    Symbol,
}

/// <summary>
/// The answer of <see cref="IContextService.SearchDetailedAsync"/>: the hits and where the time went. Added in contract 1.3
/// (work item 2.11).
/// </summary>
[DataContract]
public sealed class SearchResponse
{
    /// <summary>Gets or sets the hits, most relevant first.</summary>
    [DataMember(Order = 0)]
    public SearchResult[] Results { get; set; } = [];

    /// <summary>Gets or sets the milliseconds spent on the lexical (BM25) candidates.</summary>
    [DataMember(Order = 1)]
    public double LexicalMs { get; set; }

    /// <summary>Gets or sets the milliseconds spent on the vector candidates, including embedding the query.</summary>
    [DataMember(Order = 2)]
    public double VectorMs { get; set; }

    /// <summary>Gets or sets the milliseconds spent fusing, expanding and capping the candidates.</summary>
    [DataMember(Order = 3)]
    public double FusionMs { get; set; }

    /// <summary>Gets or sets the milliseconds the whole search took in the host.</summary>
    [DataMember(Order = 4)]
    public double TotalMs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the time budget cut retrieval short (for example the vector candidates were
    /// skipped because the embedding model was slow), so the results may be worse than usual.
    /// </summary>
    [DataMember(Order = 5)]
    public bool Degraded { get; set; }
}
