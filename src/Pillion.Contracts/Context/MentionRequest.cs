using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Asks the host to resolve an <c>@</c> mention typed in chat.</summary>
[DataContract]
public sealed class MentionRequest
{
    /// <summary>
    /// Gets or sets the mention text, for example <c>@file:Program.cs</c>, <c>@Customer.cs</c> or <c>@selection</c>. The
    /// host reads the kind from the text (<c>@kind:value</c>) when <see cref="Kind"/> is empty.
    /// </summary>
    [DataMember(Order = 0)]
    public string Mention { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the kind of mention: <c>file</c>, <c>folder</c>, <c>selection</c>, <c>errors</c> or <c>clipboard</c>.
    /// Empty means the kind comes from the mention text, and a bare <c>@name</c> is a fuzzy match (a file, else a folder).
    /// </summary>
    [DataMember(Order = 1)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the solution or folder roots the client has open; relative paths resolve against them.</summary>
    [DataMember(Order = 2)]
    public string[] WorkspaceRoots { get; set; } = [];

    /// <summary>
    /// Gets or sets the full paths of the files in the solution or folder, for fuzzy matching of <c>file</c> and
    /// <c>folder</c> mentions. Phase 1 has no index, so the client supplies the list; it may be empty when the mention is
    /// an exact path.
    /// </summary>
    [DataMember(Order = 3)]
    public string[] CandidateFiles { get; set; } = [];

    /// <summary>
    /// Gets or sets the text only the client has: the selection, the clipboard text, or the diagnostics for
    /// <c>errors</c> as JSON lines (one object per line with <c>severity</c>, <c>id</c>, <c>message</c>, <c>file</c>,
    /// <c>line</c>). <see langword="null"/> for <c>file</c> and <c>folder</c>.
    /// </summary>
    [DataMember(Order = 4)]
    public string? Payload { get; set; }

    /// <summary>Gets or sets the most tokens the resolved content may take; longer content is truncated on a line boundary. 0 means the host default.</summary>
    [DataMember(Order = 5)]
    public int MaxTokens { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user approved pinning a file that the exclusion globs or the secret-file
    /// rules would otherwise refuse. The approval is per file and per request.
    /// </summary>
    [DataMember(Order = 6)]
    public bool ApprovedSensitive { get; set; }
}
