using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The diagnostics for a file or the solution.</summary>
[DataContract]
public sealed class DiagnosticsResponse
{
    /// <summary>Gets or sets the diagnostics.</summary>
    [DataMember(Order = 0)]
    public IReadOnlyList<DiagnosticInfo> Diagnostics { get; set; } = Array.Empty<DiagnosticInfo>();
}
