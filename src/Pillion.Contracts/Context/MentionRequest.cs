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
    /// Gets or sets the kind of mention, one of the <see cref="MentionKinds"/>: <c>file</c>, <c>folder</c>, <c>selection</c>,
    /// <c>errors</c> or <c>clipboard</c>, and from contract 1.3 <c>symbol</c>, <c>docs</c>, <c>solution</c>, <c>git</c>,
    /// <c>output</c>, <c>build</c> and <c>tests</c>. Empty means the kind comes from the mention text, and a bare
    /// <c>@name</c> is a fuzzy match (a file, else a folder; from 1.3 also a symbol or a document).
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

    /// <summary>
    /// Gets or sets the kind the user typed in the <c>@kind:value</c> form (for example <c>symbol</c> in
    /// <c>@symbol:OrderService</c>), as opposed to <see cref="Kind"/>, which the client may have inferred or chosen in the
    /// picker. Empty for a bare <c>@name</c>. A typed qualifier is an explicit disambiguation: the host resolves only that
    /// kind and does not fall back to fuzzy matching. Added in contract 1.3 (work item 2.13).
    /// </summary>
    [DataMember(Order = 7)]
    public string Qualifier { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the arguments after the value, for kinds that take them. For <c>git</c>: <c>diff</c> (the working-tree
    /// diff, the default), <c>staged</c>, <c>log</c> with an optional count (<c>["log", "5"]</c>) or a revision range
    /// (<c>["HEAD~3..HEAD"]</c>); for <c>output</c>: the pane (<c>build</c>, <c>debug</c> or <c>tests</c>). Empty for the
    /// other kinds. Added in contract 1.3 (work item 2.13).
    /// </summary>
    [DataMember(Order = 8)]
    public string[] Arguments { get; set; } = [];
}
