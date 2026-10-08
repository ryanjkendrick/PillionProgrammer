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
}
