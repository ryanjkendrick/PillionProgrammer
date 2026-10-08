using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A single workspace search hit.</summary>
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
}
