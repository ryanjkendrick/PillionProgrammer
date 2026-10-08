using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Information about a symbol.</summary>
[DataContract]
public sealed class SymbolResponse
{
    /// <summary>Gets or sets a value indicating whether a symbol was found at the location.</summary>
    [DataMember(Order = 0)]
    public bool IsFound { get; set; }

    /// <summary>Gets or sets the symbol's fully qualified name.</summary>
    [DataMember(Order = 1)]
    public string? Name { get; set; }

    /// <summary>Gets or sets the symbol kind, for example <c>Method</c>.</summary>
    [DataMember(Order = 2)]
    public string? Kind { get; set; }

    /// <summary>Gets or sets the location of the symbol's definition, if it is in source.</summary>
    [DataMember(Order = 3)]
    public SourceLocation? Definition { get; set; }
}
