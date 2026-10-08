using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A set of file edits proposed or applied as one unit.</summary>
[DataContract]
public sealed class Changeset
{
    /// <summary>Gets or sets the changeset id, used for preview, apply and revert.</summary>
    [DataMember(Order = 0)]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the edits in the order they are applied.</summary>
    [DataMember(Order = 1)]
    public IReadOnlyList<FileEdit> Edits { get; set; } = Array.Empty<FileEdit>();

    /// <summary>Gets or sets a short description of the change for the user.</summary>
    [DataMember(Order = 2)]
    public string Description { get; set; } = string.Empty;
}
