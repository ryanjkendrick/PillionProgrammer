using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A position in a source file.</summary>
[DataContract]
public sealed class SourceLocation
{
    /// <summary>Gets or sets the full path of the file.</summary>
    [DataMember(Order = 0)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based line.</summary>
    [DataMember(Order = 1)]
    public int Line { get; set; }

    /// <summary>Gets or sets the zero-based column.</summary>
    [DataMember(Order = 2)]
    public int Column { get; set; }
}
