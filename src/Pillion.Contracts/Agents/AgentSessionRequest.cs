using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Starts an agent session. Members from <see cref="ConversationId"/> on were added in contract 1.4 (work item 3.5).</summary>
[DataContract]
public sealed class AgentSessionRequest
{
    /// <summary>Gets or sets the user's description of what the agent should do.</summary>
    [DataMember(Order = 0)]
    public string Goal { get; set; } = string.Empty;

    /// <summary>Gets or sets the model profile.</summary>
    [DataMember(Order = 1)]
    public Profile Profile { get; set; }

    /// <summary>Gets or sets an explicit model id, or <see langword="null"/> to choose by <see cref="Profile"/>.</summary>
    [DataMember(Order = 2)]
    public string? ModelId { get; set; }

    /// <summary>
    /// Gets or sets the conversation the task starts from (the Chat window's <see cref="ChatRequest.ConversationId"/>): its
    /// history is the session's context and the ledger is stored with it. Empty starts a session with no conversation. Added in 1.4.
    /// </summary>
    [DataMember(Order = 3)]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Gets or sets the conversation's pins, resolved again by the host (never the content). Added in 1.4.</summary>
    [DataMember(Order = 4)]
    public ConversationPin[] Pins { get; set; } = Array.Empty<ConversationPin>();

    /// <summary>Gets or sets the approval mode; <see cref="AgentApprovalMode.Ask"/> (the default) is what a 1.3 client gets. Added in 1.4.</summary>
    [DataMember(Order = 5)]
    public AgentApprovalMode ApprovalMode { get; set; }
}
