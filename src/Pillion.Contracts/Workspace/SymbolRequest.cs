using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Requests information about the symbol at a location.</summary>
[DataContract]
public sealed class SymbolRequest
{
    /// <summary>Gets or sets the location of the symbol.</summary>
    [DataMember(Order = 0)]
    public SourceLocation Location { get; set; } = new SourceLocation();
}
