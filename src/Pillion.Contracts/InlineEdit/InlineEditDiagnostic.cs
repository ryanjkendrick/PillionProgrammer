using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A diagnostic reported on the scope of an inline edit (attached for <c>/fix</c>).</summary>
[DataContract]
public sealed class InlineEditDiagnostic
{
    /// <summary>Gets or sets the zero-based line in the document.</summary>
    [DataMember(Order = 0)]
    public int Line { get; set; }

    /// <summary>Gets or sets the severity.</summary>
    [DataMember(Order = 1)]
    public DiagnosticSeverity Severity { get; set; }

    /// <summary>Gets or sets the diagnostic code, for example <c>CS0103</c>.</summary>
    [DataMember(Order = 2)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the diagnostic message.</summary>
    [DataMember(Order = 3)]
    public string Message { get; set; } = string.Empty;
}
