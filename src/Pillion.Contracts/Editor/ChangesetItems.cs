using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>What a changeset does to one file. An integer on the wire. Added in contract 1.4 (work item 3.5).</summary>
public enum FileChangeKind
{
    /// <summary>A search-and-replace edit (<see cref="FileEdit"/>).</summary>
    Edit,

    /// <summary>A whole-file write of a new file (<see cref="FileCreate"/>), or the restore of an existing one by a revert.</summary>
    Create,

    /// <summary>A deletion (<see cref="FileDelete"/>).</summary>
    Delete,

    /// <summary>A rename or move (<see cref="FileRename"/>).</summary>
    Rename,
}

/// <summary>
/// A whole-file write in a <see cref="Changeset"/>: a new file, or the restored content of an existing one when the changeset
/// is a revert (<see cref="Changeset.RevertsChangesetId"/>). Added in contract 1.4.
/// </summary>
[DataContract]
public sealed class FileCreate
{
    /// <summary>Gets or sets the full path of the file.</summary>
    [DataMember(Order = 0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's whole content, with every redaction placeholder already restored.</summary>
    [DataMember(Order = 1)]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hash the file's current content must have for the write to proceed (the post-apply hash the VSIX
    /// committed for it, see <see cref="ApplyFileHash.PostApplyHash"/>), or <see langword="null"/> when the file is new and
    /// must not exist. On a mismatch the VSIX does not overwrite: it opens a merge view for a revert and fails the file otherwise.
    /// </summary>
    [DataMember(Order = 2)]
    public string? ExpectedHash { get; set; }
}

/// <summary>A deletion in a <see cref="Changeset"/>. Added in contract 1.4.</summary>
[DataContract]
public sealed class FileDelete
{
    /// <summary>Gets or sets the full path of the file to delete.</summary>
    [DataMember(Order = 0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hash the file's current content must have for the deletion to proceed (see
    /// <see cref="ApplyFileHash.PostApplyHash"/> for the algorithm), or <see langword="null"/> to delete whatever is there. On a
    /// mismatch the VSIX fails the file rather than delete work it has not seen.
    /// </summary>
    [DataMember(Order = 1)]
    public string? ExpectedHash { get; set; }
}

/// <summary>A rename or move in a <see cref="Changeset"/>. Added in contract 1.4.</summary>
[DataContract]
public sealed class FileRename
{
    /// <summary>Gets or sets the full path of the file before the rename.</summary>
    [DataMember(Order = 0)]
    public string OldPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the full path after the rename (a different directory moves the file).</summary>
    [DataMember(Order = 1)]
    public string NewPath { get; set; } = string.Empty;
}

/// <summary>What a <see cref="ProjectItemChange"/> does. An integer on the wire. Added in contract 1.4.</summary>
public enum ProjectItemChangeKind
{
    /// <summary>Adds the item to the project.</summary>
    Add,

    /// <summary>Removes the item from the project (the file itself is a <see cref="FileDelete"/>).</summary>
    Remove,

    /// <summary>Renames the item (the file itself is a <see cref="FileRename"/>).</summary>
    Rename,
}

/// <summary>
/// A change the VSIX makes through the project system in the same transaction as the file changes (design section 5). Needed
/// for projects that list their items explicitly (legacy <c>.csproj</c>, <c>.vcxproj</c>, shared projects); SDK-style projects
/// glob their files and need none. Added in contract 1.4.
/// </summary>
[DataContract]
public sealed class ProjectItemChange
{
    /// <summary>Gets or sets the change.</summary>
    [DataMember(Order = 0)]
    public ProjectItemChangeKind Kind { get; set; }

    /// <summary>Gets or sets the full path of the project file.</summary>
    [DataMember(Order = 1)]
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the full path of the item (before the change, for a rename).</summary>
    [DataMember(Order = 2)]
    public string ItemPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's full path after a rename, or <see langword="null"/> for the other kinds.</summary>
    [DataMember(Order = 3)]
    public string? NewItemPath { get; set; }
}

/// <summary>The outcome of one file of an apply (<see cref="ApplyResult.Files"/>). Added in contract 1.4.</summary>
[DataContract]
public sealed class FileApplyOutcome
{
    /// <summary>Gets or sets the full path of the file (the new path for a rename).</summary>
    [DataMember(Order = 0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets what the changeset did to the file.</summary>
    [DataMember(Order = 1)]
    public FileChangeKind Kind { get; set; }

    /// <summary>Gets or sets a value indicating whether the change was applied (and, for an all-or-nothing failure, not rolled back).</summary>
    [DataMember(Order = 2)]
    public bool IsApplied { get; set; }

    /// <summary>Gets or sets the file's hash after the apply (see <see cref="ApplyFileHash.PostApplyHash"/>), or <see langword="null"/> for a deleted or unapplied file.</summary>
    [DataMember(Order = 3)]
    public string? PostApplyHash { get; set; }

    /// <summary>Gets or sets why the change was not applied (checkout refused, the search text matched elsewhere, the file is gone), or <see langword="null"/>.</summary>
    [DataMember(Order = 4)]
    public string? Message { get; set; }
}

/// <summary>
/// The text of a document as the editor has it (<see cref="IEditorService.ReadDocumentAsync"/>): the buffer, unsaved changes
/// included, so the agent's edits match what the VSIX will change. Added in contract 1.4 (work item 3.5; consumed by 3.10).
/// </summary>
[DataContract]
public sealed class DocumentText
{
    /// <summary>Gets or sets the full path that was asked for.</summary>
    [DataMember(Order = 0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the document is open in the editor; when it is not, <see cref="Text"/> is <see langword="null"/> and the caller reads the disk.</summary>
    [DataMember(Order = 1)]
    public bool IsOpen { get; set; }

    /// <summary>Gets or sets a value indicating whether the open document has unsaved changes.</summary>
    [DataMember(Order = 2)]
    public bool IsDirty { get; set; }

    /// <summary>Gets or sets the buffer text of an open document, or <see langword="null"/>.</summary>
    [DataMember(Order = 3)]
    public string? Text { get; set; }

    /// <summary>Gets or sets the buffer's hash (see <see cref="ApplyFileHash.PostApplyHash"/>), or <see langword="null"/> when the document is not open.</summary>
    [DataMember(Order = 4)]
    public string? Hash { get; set; }
}
