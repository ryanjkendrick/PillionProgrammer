using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Finds references to the symbol at a location.</summary>
[DataContract]
public sealed class FindReferencesRequest
{
    /// <summary>Gets or sets the location of the symbol.</summary>
    [DataMember(Order = 0)]
    public SourceLocation Location { get; set; } = new SourceLocation();
}
