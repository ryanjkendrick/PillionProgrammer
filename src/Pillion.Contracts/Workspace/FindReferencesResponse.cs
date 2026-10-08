using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The references found for a symbol.</summary>
[DataContract]
public sealed class FindReferencesResponse
{
    /// <summary>Gets or sets the locations that reference the symbol.</summary>
    [DataMember(Order = 0)]
    public IReadOnlyList<SourceLocation> References { get; set; } = Array.Empty<SourceLocation>();
}
