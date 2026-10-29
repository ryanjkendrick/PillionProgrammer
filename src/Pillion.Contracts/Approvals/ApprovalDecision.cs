using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// The user's answer to an <see cref="ApprovalRequest"/>: the reply of <see cref="IApprovalService.RequestApprovalAsync"/> and,
/// from contract 1.4, the argument of <see cref="IAgentService.AnswerApprovalAsync"/>. Members from
/// <see cref="RememberForSession"/> on were added in 1.4 (work item 3.5).
/// </summary>
[DataContract]
public sealed class ApprovalDecision
{
    /// <summary>Gets or sets the id of the approval being answered.</summary>
    [DataMember(Order = 0)]
    public string ApprovalId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user approved the tool call.</summary>
    [DataMember(Order = 1)]
    public bool IsApproved { get; set; }

    /// <summary>Gets or sets the user's reason, if given.</summary>
    [DataMember(Order = 2)]
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the same tool with the same arguments summary runs without asking for the rest
    /// of this session. Never honoured for T3, for a sensitive path or for a tainted call (design section 8). Added in 1.4.
    /// </summary>
    [DataMember(Order = 3)]
    public bool RememberForSession { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the command (for <c>run_command</c>) or tool is added to the user-level allow-list
    /// in <c>settings.json</c>. Never honoured for T3, which cannot be allow-listed, and the taint rule still re-prompts. Added in 1.4.
    /// </summary>
    [DataMember(Order = 4)]
    public bool AddToAllowList { get; set; }

    /// <summary>
    /// Gets or sets, for an approval that covers a changeset (<see cref="ApprovalRequest.ChangesetId"/>), the user's per-hunk
    /// decision from the changeset view; <see langword="null"/> accepts or rejects the whole changeset as <see cref="IsApproved"/>
    /// says. Added in 1.4.
    /// </summary>
    [DataMember(Order = 5)]
    public PreviewDecision? Preview { get; set; }
}
