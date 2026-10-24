using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A single workspace search hit. Members from <see cref="ChunkId"/> on were added in contract 1.3 (work item 2.11); a 1.2 host leaves them at their defaults.</summary>
[DataContract]
public sealed class SearchResult
{
    /// <summary>Gets or sets the full path of the file containing the hit.</summary>
    [DataMember(Order = 0)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based first line of the hit.</summary>
    [DataMember(Order = 1)]
    public int StartLine { get; set; }

    /// <summary>Gets or sets the zero-based last line of the hit.</summary>
    [DataMember(Order = 2)]
    public int EndLine { get; set; }

    /// <summary>Gets or sets the matching text.</summary>
    [DataMember(Order = 3)]
    public string Snippet { get; set; } = string.Empty;

    /// <summary>Gets or sets the relevance score; higher is more relevant.</summary>
    [DataMember(Order = 4)]
    public double Score { get; set; }

    /// <summary>
    /// Gets or sets the chunk's id: opaque to the client, unique across the indexes of one host (the host forms it from
    /// the index id and the chunk's row id). Citations carry it back.
    /// </summary>
    [DataMember(Order = 5)]
    public string ChunkId { get; set; } = string.Empty;

    /// <summary>Gets or sets the chunk's breadcrumb, <c>path &gt; namespace &gt; type &gt; member</c>.</summary>
    [DataMember(Order = 6)]
    public string Breadcrumb { get; set; } = string.Empty;

    /// <summary>Gets or sets what the chunk is, for example <c>method</c>, <c>type-header</c> or <c>window</c> (the chunker's kind).</summary>
    [DataMember(Order = 7)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the partition of the chunk's file (project or top-level folder).</summary>
    [DataMember(Order = 8)]
    public string Partition { get; set; } = string.Empty;

    /// <summary>Gets or sets the language of the chunk's file.</summary>
    [DataMember(Order = 9)]
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the hit's one-based rank in the lexical (BM25) candidates, or 0 when it was not among them.</summary>
    [DataMember(Order = 10)]
    public int LexicalRank { get; set; }

    /// <summary>Gets or sets the hit's one-based rank in the vector candidates, or 0 when it was not among them.</summary>
    [DataMember(Order = 11)]
    public int VectorRank { get; set; }

    /// <summary>Gets or sets the Reciprocal Rank Fusion score (before any reranking and boosts); higher is more relevant.</summary>
    [DataMember(Order = 12)]
    public double FusedScore { get; set; }

    /// <summary>Gets or sets the full chunk text when <see cref="SearchRequest.IncludeText"/> was set; otherwise <see langword="null"/>.</summary>
    [DataMember(Order = 13)]
    public string? Text { get; set; }
}
