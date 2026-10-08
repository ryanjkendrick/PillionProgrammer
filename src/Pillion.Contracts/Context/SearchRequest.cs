using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A search over the workspace index.</summary>
[DataContract]
public sealed class SearchRequest
{
    /// <summary>Gets or sets the natural-language or keyword query.</summary>
    [DataMember(Order = 0)]
    public string Query { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum number of results to return.</summary>
    [DataMember(Order = 1)]
    public int MaxResults { get; set; }
}
