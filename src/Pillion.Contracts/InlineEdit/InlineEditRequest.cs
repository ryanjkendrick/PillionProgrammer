using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>An inline chat request: an instruction about one scope of code in an open document.</summary>
[DataContract]
public sealed class InlineEditRequest
{
    /// <summary>Gets or sets the caller-assigned id used to correlate chunks and cancel the request.</summary>
    [DataMember(Order = 0)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Gets or sets the full path of the document.</summary>
    [DataMember(Order = 1)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the language identifier of the document (for example <c>csharp</c> or <c>vb</c>).</summary>
    [DataMember(Order = 2)]
    public string LanguageId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text of the scope as it is in the buffer: the selection, else the enclosing member, else the
    /// current line. The proposal replaces exactly this text.
    /// </summary>
    [DataMember(Order = 3)]
    public string ScopeText { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based line in the document where the scope starts.</summary>
    [DataMember(Order = 4)]
    public int ScopeStartLine { get; set; }

    /// <summary>Gets or sets the zero-based column in the document where the scope starts.</summary>
    [DataMember(Order = 5)]
    public int ScopeStartColumn { get; set; }

    /// <summary>
    /// Gets or sets the document text before the scope, up to its first character (capped by the VSIX, keeping the end
    /// nearest the scope). Context only; it is never edited.
    /// </summary>
    [DataMember(Order = 6)]
    public string TextBefore { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the document text after the scope, from the character after its last (capped by the VSIX, keeping
    /// the start nearest the scope). Context only; it is never edited.
    /// </summary>
    [DataMember(Order = 7)]
    public string TextAfter { get; set; } = string.Empty;

    /// <summary>Gets or sets what the user typed, without the slash shortcut (may be empty when a shortcut is given).</summary>
    [DataMember(Order = 8)]
    public string Instruction { get; set; } = string.Empty;

    /// <summary>Gets or sets the slash shortcut, or <see cref="InlineEditCommand.None"/>.</summary>
    [DataMember(Order = 9)]
    public InlineEditCommand Command { get; set; }

    /// <summary>Gets or sets the diagnostics on the scope's lines (the subject of <c>/fix</c>; shown to the model for every command).</summary>
    [DataMember(Order = 10)]
    public IReadOnlyList<InlineEditDiagnostic> Diagnostics { get; set; } = Array.Empty<InlineEditDiagnostic>();

    /// <summary>
    /// Gets or sets the proposal currently shown when the user refines it with a follow-up prompt; the model then edits
    /// this text rather than starting again from <see cref="ScopeText"/> (which stays the buffer's text). <see langword="null"/>
    /// for a first prompt.
    /// </summary>
    [DataMember(Order = 11)]
    public string? PreviousProposal { get; set; }

    /// <summary>Gets or sets an explicit model id (<c>providerId/modelId</c>) that overrides routing, or <see langword="null"/>.</summary>
    [DataMember(Order = 12)]
    public string? ModelId { get; set; }

    /// <summary>Gets or sets the model profile; <see cref="Profile.Auto"/> (the default) routes by the InlineEdit task class.</summary>
    [DataMember(Order = 13)]
    public Profile Profile { get; set; } = Profile.Auto;

    /// <summary>
    /// Gets or sets an id shared by every prompt of one inline chat session (the first prompt and its refinements), so
    /// the host can show the model the earlier instructions; <see langword="null"/> for none.
    /// </summary>
    [DataMember(Order = 14)]
    public string? ConversationId { get; set; }
}
