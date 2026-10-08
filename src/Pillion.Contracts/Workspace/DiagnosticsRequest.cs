using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Requests compiler and analyzer diagnostics.</summary>
[DataContract]
public sealed class DiagnosticsRequest
{
    /// <summary>Gets or sets the file to report on, or <see langword="null"/> for the whole solution.</summary>
    [DataMember(Order = 0)]
    public string? FilePath { get; set; }
}
