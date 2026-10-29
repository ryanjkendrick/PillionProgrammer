using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The VSIX's intent to apply a changeset (<see cref="IJournalService.BeginApplyAsync"/>). Added in contract 1.4.</summary>
[DataContract]
public sealed class ApplyBeginRequest
{
    /// <summary>Gets or sets the changeset about to be applied (<see cref="Changeset.Id"/>).</summary>
    [DataMember(Order = 0)]
    public string ChangesetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the agent session the changeset belongs to, or empty for a changeset from another source.</summary>
    [DataMember(Order = 1)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets every file the changeset touches, with its text before any edit.</summary>
    [DataMember(Order = 2)]
    public ApplyFileSnapshot[] Files { get; set; } = Array.Empty<ApplyFileSnapshot>();

    /// <summary>Gets or sets the parts of the changeset the user accepted (per-hunk review), or <see langword="null"/> for all of it.</summary>
    [DataMember(Order = 3)]
    public PreviewDecision? Decision { get; set; }
}

/// <summary>One file as it was before the apply, for the checkpoint. Added in contract 1.4.</summary>
[DataContract]
public sealed class ApplyFileSnapshot
{
    /// <summary>Gets or sets the full path (the old path for a rename).</summary>
    [DataMember(Order = 0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets what the changeset does to the file.</summary>
    [DataMember(Order = 1)]
    public FileChangeKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the text the VSIX is about to replace (the editor buffer when the file is open, otherwise the disk), or
    /// <see langword="null"/> for a file the changeset creates.
    /// </summary>
    [DataMember(Order = 2)]
    public string? PreEditText { get; set; }

    /// <summary>Gets or sets the full path after a rename, or <see langword="null"/> for the other kinds.</summary>
    [DataMember(Order = 3)]
    public string? NewPath { get; set; }
}

/// <summary>The ids the host assigned to an apply (<see cref="IJournalService.BeginApplyAsync"/>). Added in contract 1.4.</summary>
[DataContract]
public sealed class ApplyBeginResponse
{
    /// <summary>Gets or sets the checkpoint holding the pre-edit text (content-addressed, in the encrypted <c>checkpoints.db</c>).</summary>
    [DataMember(Order = 0)]
    public string CheckpointId { get; set; } = string.Empty;

    /// <summary>Gets or sets the journal entry, to commit or abort.</summary>
    [DataMember(Order = 1)]
    public string JournalId { get; set; } = string.Empty;
}

/// <summary>The commit of an apply (<see cref="IJournalService.CommitApplyAsync"/>). Added in contract 1.4.</summary>
[DataContract]
public sealed class ApplyCommitRequest
{
    /// <summary>Gets or sets the journal entry being committed.</summary>
    [DataMember(Order = 0)]
    public string JournalId { get; set; } = string.Empty;

    /// <summary>Gets or sets the hash of every file as the VSIX left it.</summary>
    [DataMember(Order = 1)]
    public ApplyFileHash[] Files { get; set; } = Array.Empty<ApplyFileHash>();
}

/// <summary>One file's hash after an apply. Added in contract 1.4.</summary>
[DataContract]
public sealed class ApplyFileHash
{
    /// <summary>Gets or sets the full path (the new path for a rename).</summary>
    [DataMember(Order = 0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file's hash after the apply: the lower-case hexadecimal SHA-256 of the text encoded as UTF-8 without a
    /// byte-order mark, computed by the VSIX over the text it wrote (line endings as written). The host stores and echoes it
    /// (<see cref="FileCreate.ExpectedHash"/>, <see cref="ApplyJournalFile.PostApplyHash"/>) and never recomputes it. <see langword="null"/>
    /// for a deleted file.
    /// </summary>
    [DataMember(Order = 1)]
    public string? PostApplyHash { get; set; }
}

/// <summary>Where a journal entry is (design section 10: intent, committed, aborted). An integer on the wire. Added in contract 1.4.</summary>
public enum ApplyJournalStatus
{
    /// <summary>The intent is recorded; the apply is in progress or was interrupted.</summary>
    Intent,

    /// <summary>The apply completed and its post-apply hashes are recorded.</summary>
    Committed,

    /// <summary>The apply failed and was rolled back, or an interrupted apply was rolled back at start-up.</summary>
    Aborted,
}

/// <summary>A journal entry as <see cref="IJournalService.ListInterruptedAppliesAsync"/> returns it. Added in contract 1.4.</summary>
[DataContract]
public sealed class ApplyJournalEntry
{
    /// <summary>Gets or sets the journal id.</summary>
    [DataMember(Order = 0)]
    public string JournalId { get; set; } = string.Empty;

    /// <summary>Gets or sets the checkpoint holding the pre-edit text.</summary>
    [DataMember(Order = 1)]
    public string CheckpointId { get; set; } = string.Empty;

    /// <summary>Gets or sets the changeset being applied.</summary>
    [DataMember(Order = 2)]
    public string ChangesetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the agent session, or empty.</summary>
    [DataMember(Order = 3)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the entry's state (<see cref="ApplyJournalStatus.Intent"/> for an interrupted apply).</summary>
    [DataMember(Order = 4)]
    public ApplyJournalStatus Status { get; set; }

    /// <summary>Gets or sets when the intent was recorded.</summary>
    [DataMember(Order = 5)]
    public DateTime BeganUtc { get; set; }

    /// <summary>Gets or sets when the entry was committed or aborted, or <see langword="null"/>.</summary>
    [DataMember(Order = 6)]
    public DateTime? EndedUtc { get; set; }

    /// <summary>Gets or sets every file of the apply with its pre-edit text (roll-back) and, once committed, its post-apply hash.</summary>
    [DataMember(Order = 7)]
    public ApplyJournalFile[] Files { get; set; } = Array.Empty<ApplyJournalFile>();

    /// <summary>Gets or sets the changeset itself, for roll-forward, or <see langword="null"/> when the host no longer has it.</summary>
    [DataMember(Order = 8)]
    public Changeset? Changeset { get; set; }

    /// <summary>Gets or sets the parts of the changeset the user had accepted (<see cref="ApplyBeginRequest.Decision"/>), or <see langword="null"/> for all of it.</summary>
    [DataMember(Order = 9)]
    public PreviewDecision? Decision { get; set; }
}

/// <summary>One file of a journal entry. Added in contract 1.4.</summary>
[DataContract]
public sealed class ApplyJournalFile
{
    /// <summary>Gets or sets the full path (the old path for a rename).</summary>
    [DataMember(Order = 0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets what the changeset does to the file.</summary>
    [DataMember(Order = 1)]
    public FileChangeKind Kind { get; set; }

    /// <summary>Gets or sets the text before the apply, from the checkpoint, or <see langword="null"/> for a created file.</summary>
    [DataMember(Order = 2)]
    public string? PreEditText { get; set; }

    /// <summary>Gets or sets the hash after the apply (<see cref="ApplyFileHash.PostApplyHash"/>), or <see langword="null"/> before the commit and for a deleted file.</summary>
    [DataMember(Order = 3)]
    public string? PostApplyHash { get; set; }

    /// <summary>Gets or sets the full path after a rename, or <see langword="null"/>.</summary>
    [DataMember(Order = 4)]
    public string? NewPath { get; set; }
}
