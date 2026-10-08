using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The result of applying a changeset in the editor.</summary>
[DataContract]
public sealed class ApplyResult
{
    /// <summary>Gets or sets the id of the changeset that was applied.</summary>
    [DataMember(Order = 0)]
    public string ChangesetId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether every edit was applied.</summary>
    [DataMember(Order = 1)]
    public bool IsSuccess { get; set; }

    /// <summary>Gets or sets the paths of files whose edit could not be applied.</summary>
    [DataMember(Order = 2)]
    public IReadOnlyList<string> FailedPaths { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets a description of any failure.</summary>
    [DataMember(Order = 3)]
    public string? Message { get; set; }
}
