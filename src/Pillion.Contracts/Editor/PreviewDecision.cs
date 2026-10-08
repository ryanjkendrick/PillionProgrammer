using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The user's decision after previewing a changeset.</summary>
[DataContract]
public sealed class PreviewDecision
{
    /// <summary>Gets or sets the id of the changeset that was previewed.</summary>
    [DataMember(Order = 0)]
    public string ChangesetId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user accepted the changeset.</summary>
    [DataMember(Order = 1)]
    public bool IsAccepted { get; set; }
}
