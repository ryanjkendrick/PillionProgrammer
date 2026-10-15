using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The result of resolving a mention.</summary>
[DataContract]
public sealed class MentionResolution
{
    /// <summary>Gets or sets the mention that was resolved.</summary>
    [DataMember(Order = 0)]
    public string Mention { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the mention matched anything.</summary>
    [DataMember(Order = 1)]
    public bool IsResolved { get; set; }

    /// <summary>Gets or sets the file the mention refers to, if any.</summary>
    [DataMember(Order = 2)]
    public string? FilePath { get; set; }

    /// <summary>Gets or sets the content to attach to the prompt, if any.</summary>
    [DataMember(Order = 3)]
    public string? Content { get; set; }

    /// <summary>Gets or sets the kind the mention resolved as, one of the <see cref="MentionKinds"/>.</summary>
    [DataMember(Order = 4)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the short name to show on the pin, for example the file name.</summary>
    [DataMember(Order = 5)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the token count of <see cref="Content"/>, for the default model, after any truncation.</summary>
    [DataMember(Order = 6)]
    public int TokenCount { get; set; }

    /// <summary>Gets or sets a value indicating whether <see cref="Content"/> was cut to fit <see cref="MentionRequest.MaxTokens"/>.</summary>
    [DataMember(Order = 7)]
    public bool Truncated { get; set; }

    /// <summary>Gets or sets up to 10 full paths when a fuzzy match is ambiguous; the client asks the user to pick one.</summary>
    [DataMember(Order = 8)]
    public string[] Candidates { get; set; } = [];

    /// <summary>Gets or sets why the mention was not resolved (empty when it was).</summary>
    [DataMember(Order = 9)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the file was refused only because it is excluded or looks secret, so the
    /// client can ask the user for approval and resolve again with <see cref="MentionRequest.ApprovedSensitive"/>.
    /// </summary>
    [DataMember(Order = 10)]
    public bool RequiresApproval { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the content came from an excluded or secret file that the user approved.
    /// Such a pin is never sent to a cloud model.
    /// </summary>
    [DataMember(Order = 11)]
    public bool IsSensitive { get; set; }

    /// <summary>
    /// Gets or sets the spans the content was built from (for example a symbol's definition and the references summarised,
    /// or the chunks of a <c>docs</c> match), so the pin can open them; <see cref="Citation.ReplyOffset"/> is -1. Empty for
    /// content that has no source location. Added in contract 1.3 (work item 2.13).
    /// </summary>
    [DataMember(Order = 12)]
    public Citation[] Citations { get; set; } = [];
}
