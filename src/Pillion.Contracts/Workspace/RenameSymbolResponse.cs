using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The result of a rename.</summary>
[DataContract]
public sealed class RenameSymbolResponse
{
    /// <summary>Gets or sets a value indicating whether the rename was applied.</summary>
    [DataMember(Order = 0)]
    public bool IsSuccess { get; set; }

    /// <summary>Gets or sets the paths of the files that changed.</summary>
    [DataMember(Order = 1)]
    public IReadOnlyList<string> ChangedFiles { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets a description of any failure.</summary>
    [DataMember(Order = 2)]
    public string? Message { get; set; }
}
