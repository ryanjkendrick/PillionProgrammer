using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A single diagnostic.</summary>
[DataContract]
public sealed class DiagnosticInfo
{
    /// <summary>Gets or sets the diagnostic id, for example <c>CS0103</c>.</summary>
    [DataMember(Order = 0)]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the diagnostic message.</summary>
    [DataMember(Order = 1)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the diagnostic severity.</summary>
    [DataMember(Order = 2)]
    public DiagnosticSeverity Severity { get; set; }

    /// <summary>Gets or sets where the diagnostic was reported.</summary>
    [DataMember(Order = 3)]
    public SourceLocation Location { get; set; } = new SourceLocation();
}
