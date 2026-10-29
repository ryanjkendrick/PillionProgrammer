using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// The user's decision after previewing a changeset: the reply of <see cref="IEditorService.ShowPreviewAsync"/> and, from
/// contract 1.4, the per-hunk part of an <see cref="ApprovalDecision"/>. Members from <see cref="AcceptedEditIndices"/> on were
/// added in 1.4 (work item 3.5; consumed by 3.10 and 3.13): the changeset view accepts and rejects per hunk, and the host
/// re-baselines its in-memory workspace from what was applied.
/// </summary>
[DataContract]
public sealed class PreviewDecision
{
    /// <summary>Gets or sets the id of the changeset that was previewed.</summary>
    [DataMember(Order = 0)]
    public string ChangesetId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user accepted the changeset (any part of it).</summary>
    [DataMember(Order = 1)]
    public bool IsAccepted { get; set; }

    /// <summary>
    /// Gets or sets the indices into <see cref="Changeset.Edits"/> the user accepted, in ascending order; <see langword="null"/>
    /// (what a 1.3 VSIX sends) means every edit when <see cref="IsAccepted"/> is set. Added in 1.4.
    /// </summary>
    [DataMember(Order = 2)]
    public int[]? AcceptedEditIndices { get; set; }

    /// <summary>Gets or sets the indices into <see cref="Changeset.Creates"/> the user accepted; <see langword="null"/> means all. Added in 1.4.</summary>
    [DataMember(Order = 3)]
    public int[]? AcceptedCreateIndices { get; set; }

    /// <summary>Gets or sets the indices into <see cref="Changeset.Deletes"/> the user accepted; <see langword="null"/> means all. Added in 1.4.</summary>
    [DataMember(Order = 4)]
    public int[]? AcceptedDeleteIndices { get; set; }

    /// <summary>
    /// Gets or sets the indices into <see cref="Changeset.Renames"/> the user accepted; <see langword="null"/> means all. A
    /// <see cref="ProjectItemChange"/> follows the file change it belongs to. Added in 1.4.
    /// </summary>
    [DataMember(Order = 5)]
    public int[]? AcceptedRenameIndices { get; set; }
}
