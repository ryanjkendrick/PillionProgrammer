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
}
