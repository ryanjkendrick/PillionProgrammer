using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// A <c>file:line</c> span that a chat reply or a mention used, so the Chat window can make it clickable (design section 7,
/// Hybrid retrieval, step 5). Added in contract 1.3 (work item 2.12).
/// </summary>
[DataContract]
public sealed class Citation
{
    /// <summary>Gets or sets the full path of the file.</summary>
    [DataMember(Order = 0)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based first line of the span.</summary>
    [DataMember(Order = 1)]
    public int StartLine { get; set; }

    /// <summary>Gets or sets the zero-based last line of the span (inclusive).</summary>
    [DataMember(Order = 2)]
    public int EndLine { get; set; }

    /// <summary>Gets or sets the chunk the span came from (see <see cref="SearchResult.ChunkId"/>), or empty when it did not come from the index.</summary>
    [DataMember(Order = 3)]
    public string ChunkId { get; set; } = string.Empty;

    /// <summary>Gets or sets the chunk's breadcrumb, <c>path &gt; namespace &gt; type &gt; member</c>, or empty.</summary>
    [DataMember(Order = 4)]
    public string Breadcrumb { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where in the reply the citation applies: the zero-based UTF-16 offset into the reply text (all chunks'
    /// <see cref="ChatChunk.Text"/> concatenated) just after the statement it supports, or -1 when it applies to the reply
    /// as a whole (and for a mention's citations).
    /// </summary>
    [DataMember(Order = 5)]
    public int ReplyOffset { get; set; } = -1;
}
