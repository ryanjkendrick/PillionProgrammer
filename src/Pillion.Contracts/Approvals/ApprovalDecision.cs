using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The user's answer to an <see cref="ApprovalRequest"/>.</summary>
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
}
