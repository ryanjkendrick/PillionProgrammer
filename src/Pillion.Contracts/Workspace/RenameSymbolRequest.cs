using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Renames the symbol at a location across the solution.</summary>
[DataContract]
public sealed class RenameSymbolRequest
{
    /// <summary>Gets or sets the location of the symbol.</summary>
    [DataMember(Order = 0)]
    public SourceLocation Location { get; set; } = new SourceLocation();

    /// <summary>Gets or sets the new name.</summary>
    [DataMember(Order = 1)]
    public string NewName { get; set; } = string.Empty;
}
