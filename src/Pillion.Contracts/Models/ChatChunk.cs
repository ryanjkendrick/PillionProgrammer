using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A streamed piece of a chat response.</summary>
[DataContract]
public sealed class ChatChunk
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

    /// <summary>Gets or sets the provider's id of the model that produced this chunk (the reply header names it).</summary>
    [DataMember(Order = 3)]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the id of the provider that produced this chunk.</summary>
    [DataMember(Order = 4)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of prompt tokens, reported on the final chunk (0 when unknown or not final).</summary>
    [DataMember(Order = 5)]
    public int PromptTokens { get; set; }

    /// <summary>Gets or sets the number of generated tokens, reported on the final chunk (0 when unknown or not final).</summary>
    [DataMember(Order = 6)]
    public int CompletionTokens { get; set; }

    /// <summary>
    /// Gets or sets the error that ended the stream after some text had already been sent, on a final chunk; otherwise
    /// <see langword="null"/>. A failure before any text is reported by the call failing instead.
    /// </summary>
    [DataMember(Order = 7)]
    public PillionError? Error { get; set; }

    /// <summary>
    /// Gets or sets the <c>file:line</c> spans the reply cites, sent with the final chunk when the reply used retrieved
    /// chunks or pins with a location; empty on every other chunk and from a 1.2 host. Added in contract 1.3 (work item 2.12).
    /// </summary>
    [DataMember(Order = 8)]
    public Citation[] Citations { get; set; } = [];
}