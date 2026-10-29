using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// A set of file changes proposed or applied as one unit (design section 5, Changeset preview and apply). Contract 1.0 carried
/// search-and-replace edits only; members from <see cref="Creates"/> on were added in contract 1.4 (work item 3.5; consumed by
/// 3.10 and 3.13). The VSIX applies everything all-or-nothing under one linked undo transaction; a 1.3 VSIX reads only
/// <see cref="Edits"/>, so a host must not send it a changeset that relies on the other members. The changeset carries no
/// checkpoint or journal id: those are the host's records, created when the VSIX begins the apply
/// (<see cref="IJournalService.BeginApplyAsync"/>).
/// </summary>
[DataContract]
public sealed class Changeset
{
    /// <summary>Gets or sets the changeset id, used for preview, apply and revert.</summary>
    [DataMember(Order = 0)]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the edits in the order they are applied.</summary>
    [DataMember(Order = 1)]
    public IReadOnlyList<FileEdit> Edits { get; set; } = Array.Empty<FileEdit>();

    /// <summary>Gets or sets a short description of the change for the user.</summary>
    [DataMember(Order = 2)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the files to create (whole-file writes), applied after <see cref="Edits"/>. Added in 1.4.</summary>
    [DataMember(Order = 3)]
    public FileCreate[] Creates { get; set; } = Array.Empty<FileCreate>();

    /// <summary>Gets or sets the files to delete, applied after <see cref="Creates"/>. Added in 1.4.</summary>
    [DataMember(Order = 4)]
    public FileDelete[] Deletes { get; set; } = Array.Empty<FileDelete>();

    /// <summary>Gets or sets the files to rename or move, applied after <see cref="Deletes"/>; an edit to a renamed file names its old path. Added in 1.4.</summary>
    [DataMember(Order = 5)]
    public FileRename[] Renames { get; set; } = Array.Empty<FileRename>();

    /// <summary>
    /// Gets or sets the project-item changes the VSIX makes through the project system in the same transaction (adds for
    /// <see cref="Creates"/>, removes for <see cref="Deletes"/>, renames for <see cref="Renames"/>, on projects that list items
    /// explicitly). Added in 1.4.
    /// </summary>
    [DataMember(Order = 6)]
    public ProjectItemChange[] ProjectItemChanges { get; set; } = Array.Empty<ProjectItemChange>();

    /// <summary>
    /// Gets or sets the paths among the changes that are sensitive (design section 8: project and build files, scripts, test
    /// files, <c>.pillion/</c>), which the changeset view marks and which need explicit approval in every mode. Added in 1.4.
    /// </summary>
    [DataMember(Order = 7)]
    public string[] SensitivePaths { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the id of an earlier applied changeset this one restores (Revert changeset and Revert session), or
    /// <see langword="null"/> for an ordinary changeset. The host builds a restore from the checkpoint: whole-file writes in
    /// <see cref="Creates"/> for files that existed, <see cref="Deletes"/> for files the earlier changeset created, the earlier
    /// renames reversed. The VSIX may undo the earlier changeset's linked undo transaction instead while it is still undoable, and
    /// opens a merge view for a file whose <see cref="FileCreate.ExpectedHash"/> no longer matches. Added in 1.4.
    /// </summary>
    [DataMember(Order = 8)]
    public string? RevertsChangesetId { get; set; }
}
