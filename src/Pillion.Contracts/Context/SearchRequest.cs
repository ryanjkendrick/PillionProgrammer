using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A search over the workspace index. Members from <see cref="Roots"/> on were added in contract 1.3 (work item 2.11); a 1.2 host ignores them.</summary>
[DataContract]
public sealed class SearchRequest
{
    /// <summary>Gets or sets the natural-language or keyword query.</summary>
    [DataMember(Order = 0)]
    public string Query { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum number of results to return.</summary>
    [DataMember(Order = 1)]
    public int MaxResults { get; set; }

    /// <summary>Gets or sets the repository roots to search (as <see cref="IndexRootStatus.RootPath"/>); empty searches every root of the open solution.</summary>
    [DataMember(Order = 2)]
    public string[] Roots { get; set; } = [];

    /// <summary>Gets or sets the partitions (project or top-level folder, design section 7) to keep; empty keeps all.</summary>
    [DataMember(Order = 3)]
    public string[] Partitions { get; set; } = [];

    /// <summary>Gets or sets the languages to keep (<c>csharp</c>, <c>typescript</c> and so on); empty keeps all.</summary>
    [DataMember(Order = 4)]
    public string[] Languages { get; set; } = [];

    /// <summary>Gets or sets how candidates are found; <see cref="SearchMode.Hybrid"/> by default.</summary>
    [DataMember(Order = 5)]
    public SearchMode Mode { get; set; }

    /// <summary>Gets or sets the most hits from one file; 0 means the host default.</summary>
    [DataMember(Order = 6)]
    public int MaxPerFile { get; set; }

    /// <summary>Gets or sets a value indicating whether each hit carries the full chunk text in <see cref="SearchResult.Text"/> (otherwise only the snippet).</summary>
    [DataMember(Order = 7)]
    public bool IncludeText { get; set; }

    /// <summary>Gets or sets the full paths of the open and recently edited files, most recent first, which get the retrieval boost.</summary>
    [DataMember(Order = 8)]
    public string[] FocusFiles { get; set; } = [];
}
