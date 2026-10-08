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
}
