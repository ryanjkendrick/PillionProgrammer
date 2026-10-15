using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A chat turn request.</summary>
[DataContract]
public sealed class ChatRequest
{
    /// <summary>Gets or sets the caller-assigned id used to correlate chunks and cancel the request.</summary>
    [DataMember(Order = 0)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Gets or sets the conversation so far, oldest first.</summary>
    [DataMember(Order = 1)]
    public IReadOnlyList<ChatMessage> Messages { get; set; } = Array.Empty<ChatMessage>();

    /// <summary>Gets or sets an explicit model id, or <see langword="null"/> to choose by <see cref="Profile"/>.</summary>
    [DataMember(Order = 2)]
    public string? ModelId { get; set; }

    /// <summary>Gets or sets the model profile.</summary>
    [DataMember(Order = 3)]
    public Profile Profile { get; set; }

    /// <summary>
    /// Gets or sets the mentions the client has already resolved and pinned to the conversation. A request with pins, a
    /// current file or project rules goes through context assembly; a plain request is sent as it is.
    /// </summary>
    [DataMember(Order = 4)]
    public MentionResolution[] Pins { get; set; } = [];

    /// <summary>Gets or sets the full path of the file open in the editor, or empty when none is.</summary>
    [DataMember(Order = 5)]
    public string CurrentFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the text around the caret, or the selection, in <see cref="CurrentFile"/>; <see langword="null"/> when not sent.</summary>
    [DataMember(Order = 6)]
    public string? CurrentFileRegion { get; set; }

    /// <summary>Gets or sets the id of the conversation; the rolling history summary is cached under it. Empty disables the cache.</summary>
    [DataMember(Order = 7)]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the contents of the repository's <c>.pillion/rules.md</c>, which the client supplies only after the
    /// workspace is trusted; <see langword="null"/> when there are none or the workspace is not trusted.
    /// </summary>
    [DataMember(Order = 8)]
    public string? ProjectRules { get; set; }

    /// <summary>
    /// Gets or sets whether the host retrieves code from the index for this request; <see cref="Contracts.RetrievalMode.Auto"/>
    /// (the default) lets the host decide. Added in contract 1.3 (work item 2.12); a 1.2 host ignores it.
    /// </summary>
    [DataMember(Order = 9)]
    public RetrievalMode RetrievalMode { get; set; }

    /// <summary>Gets or sets the most retrieved chunks to put in the context; 0 means the host default. Added in contract 1.3 (work item 2.12).</summary>
    [DataMember(Order = 10)]
    public int MaxRetrievedChunks { get; set; }

    /// <summary>
    /// Gets or sets the context items the user removed in the Context Inspector before sending: the
    /// <see cref="ContextReportItemDto.ContextKey"/> of each (a retrieved chunk's id, or <c>repoMap</c>). The host leaves them
    /// out of this request. Added in contract 1.3 (work item 2.12); a host before it ignores it.
    /// </summary>
    [DataMember(Order = 11)]
    public string[] ExcludedContextKeys { get; set; } = [];
}
