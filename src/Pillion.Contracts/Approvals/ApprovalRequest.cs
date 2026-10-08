using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Asks the user to approve a tool call.</summary>
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
}
