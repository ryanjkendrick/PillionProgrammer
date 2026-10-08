using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A streamed piece of a completion.</summary>
[DataContract]
public sealed class CompletionChunk
{
    /// <summary>Gets or sets the id of the request this chunk belongs to.</summary>
    [DataMember(Order = 0)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Gets or sets the generated text in this chunk.</summary>
    [DataMember(Order = 1)]
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is the last chunk of the stream.</summary>
    [DataMember(Order = 2)]
    public bool IsFinal { get; set; }
}
