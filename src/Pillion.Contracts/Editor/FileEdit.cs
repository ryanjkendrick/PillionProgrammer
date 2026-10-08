using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A search-and-replace edit to one file.</summary>
[DataContract]
public sealed class FileEdit
{
    /// <summary>Gets or sets the full path of the file to edit.</summary>
    [DataMember(Order = 0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the exact text to find; empty to create the file.</summary>
    [DataMember(Order = 1)]
    public string SearchText { get; set; } = string.Empty;

    /// <summary>Gets or sets the replacement text.</summary>
    [DataMember(Order = 2)]
    public string ReplaceText { get; set; } = string.Empty;
}
