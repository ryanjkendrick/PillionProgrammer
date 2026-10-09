using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A fill-in-the-middle code completion request.</summary>
[DataContract]
public sealed class CompletionRequest
{
    /// <summary>Gets or sets the caller-assigned id used to correlate chunks and cancel the request.</summary>
    [DataMember(Order = 0)]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Gets or sets the text before the caret.</summary>
    [DataMember(Order = 1)]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Gets or sets the text after the caret.</summary>
    [DataMember(Order = 2)]
    public string Suffix { get; set; } = string.Empty;

    /// <summary>Gets or sets the language identifier of the document (for example <c>csharp</c>).</summary>
    [DataMember(Order = 3)]
    public string LanguageId { get; set; } = string.Empty;

    /// <summary>Gets or sets the full path of the document.</summary>
    [DataMember(Order = 4)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum number of tokens to generate.</summary>
    [DataMember(Order = 5)]
    public int MaxTokens { get; set; }

    /// <summary>Gets or sets the task class used to route the request.</summary>
    [DataMember(Order = 6)]
    public TaskClass TaskClass { get; set; }
    /// <summary>Gets or sets the zero-based line of the caret in the document.</summary>
    [DataMember(Order = 7)]
    public int CaretLine { get; set; }

    /// <summary>Gets or sets the zero-based column of the caret in the document.</summary>
    [DataMember(Order = 8)]
    public int CaretColumn { get; set; }

    /// <summary>Gets or sets the signature or header of the declaration that encloses the caret, when known.</summary>
    [DataMember(Order = 9)]
    public string? EnclosingDeclaration { get; set; }

    /// <summary>Gets or sets the full paths of the documents open in the IDE, most recently used first.</summary>
    [DataMember(Order = 10)]
    public string[] OpenDocuments { get; set; } = [];

    /// <summary>Gets or sets short descriptions of the most recent edits (file, line and the changed text), newest first.</summary>
    [DataMember(Order = 11)]
    public string[] RecentEdits { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether a multi-line completion is wanted at this caret position.</summary>
    [DataMember(Order = 12)]
    public bool AllowMultiLine { get; set; }

    /// <summary>Gets or sets stop sequences that end the completion, beyond the model family's defaults.</summary>
    [DataMember(Order = 13)]
    public string[] StopSequences { get; set; } = [];
}