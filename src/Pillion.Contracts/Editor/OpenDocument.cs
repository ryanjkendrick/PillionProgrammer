using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A document open in the editor.</summary>
[DataContract]
public sealed class OpenDocument
{
    /// <summary>Gets or sets the full path of the document.</summary>
    [DataMember(Order = 0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the language identifier of the document.</summary>
    [DataMember(Order = 1)]
    public string LanguageId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the document has unsaved changes.</summary>
    [DataMember(Order = 2)]
    public bool IsDirty { get; set; }

    /// <summary>Gets or sets a value indicating whether the document is the active one.</summary>
    [DataMember(Order = 3)]
    public bool IsActive { get; set; }
}
