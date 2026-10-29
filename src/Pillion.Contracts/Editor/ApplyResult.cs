using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// The result of applying a changeset in the editor. Members from <see cref="Files"/> on were added in contract 1.4 (work item
/// 3.5; consumed by 3.13): the per-file outcome, whether a failure rolled the files already changed back, and the checkpoint
/// and journal the VSIX received from <see cref="IJournalService.BeginApplyAsync"/>.
/// </summary>
[DataContract]
public sealed class ApplyResult
{
    /// <summary>Gets or sets the id of the changeset that was applied.</summary>
    [DataMember(Order = 0)]
    public string ChangesetId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether every edit was applied.</summary>
    [DataMember(Order = 1)]
    public bool IsSuccess { get; set; }

    /// <summary>Gets or sets the paths of files whose edit could not be applied.</summary>
    [DataMember(Order = 2)]
    public IReadOnlyList<string> FailedPaths { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets a description of any failure.</summary>
    [DataMember(Order = 3)]
    public string? Message { get; set; }

    /// <summary>Gets or sets the outcome of every file the changeset touched, with its post-apply hash. Added in 1.4.</summary>
    [DataMember(Order = 4)]
    public FileApplyOutcome[] Files { get; set; } = Array.Empty<FileApplyOutcome>();

    /// <summary>
    /// Gets or sets a value indicating whether, after a failure, the files already changed were rolled back and the journal
    /// aborted (apply is all-or-nothing; design section 5). False with <see cref="IsSuccess"/> false means a file may be
    /// half-applied and the journal entry stays interrupted. Added in 1.4.
    /// </summary>
    [DataMember(Order = 5)]
    public bool RolledBack { get; set; }

    /// <summary>Gets or sets the checkpoint of the pre-edit text (<see cref="ApplyBeginResponse.CheckpointId"/>), empty when the apply never began. Added in 1.4.</summary>
    [DataMember(Order = 6)]
    public string CheckpointId { get; set; } = string.Empty;

    /// <summary>Gets or sets the journal entry of the apply (<see cref="ApplyBeginResponse.JournalId"/>), empty when the apply never began. Added in 1.4.</summary>
    [DataMember(Order = 7)]
    public string JournalId { get; set; } = string.Empty;
}
