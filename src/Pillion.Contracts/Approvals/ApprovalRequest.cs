using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// Asks the user to approve a tool call: the argument of <see cref="IApprovalService.RequestApprovalAsync"/> and, from contract
/// 1.4, the pending approval a session's ledger carries (<see cref="AgentSessionState.PendingApproval"/>). Members from
/// <see cref="SessionId"/> on were added in 1.4 (work item 3.5).
/// </summary>
[DataContract]
public sealed class ApprovalRequest
{
    /// <summary>Gets or sets the id used to answer the approval.</summary>
    [DataMember(Order = 0)]
    public string ApprovalId { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the tool to run.</summary>
    [DataMember(Order = 1)]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>Gets or sets the permission tier of the tool call.</summary>
    [DataMember(Order = 2)]
    public PermissionTier Tier { get; set; }

    /// <summary>Gets or sets a human-readable summary of the tool arguments.</summary>
    [DataMember(Order = 3)]
    public string ArgumentsSummary { get; set; } = string.Empty;

    /// <summary>Gets or sets where the request came from (for example the agent step that produced it).</summary>
    [DataMember(Order = 4)]
    public string Provenance { get; set; } = string.Empty;

    /// <summary>Gets or sets why the request is considered tainted by untrusted content, if it is.</summary>
    [DataMember(Order = 5)]
    public string? TaintReason { get; set; }

    /// <summary>Gets or sets the session the approval belongs to. Added in 1.4.</summary>
    [DataMember(Order = 6)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the tool call waiting on the approval (<see cref="AgentToolCallRecord.ToolCallId"/>), so the Agent window can match the two. Added in 1.4.</summary>
    [DataMember(Order = 7)]
    public string ToolCallId { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of source of the instruction; <see cref="Provenance"/> keeps the detail. Added in 1.4.</summary>
    [DataMember(Order = 8)]
    public ProvenanceKind ProvenanceKind { get; set; }

    /// <summary>Gets or sets the changeset the approval is for (a T1 apply), or <see langword="null"/> for any other call. Added in 1.4.</summary>
    [DataMember(Order = 9)]
    public string? ChangesetId { get; set; }

    /// <summary>Gets or sets when the approval was requested (time spent waiting is excluded from the session's wall-time budget). Added in 1.4.</summary>
    [DataMember(Order = 10)]
    public DateTime RequestedUtc { get; set; }
}
