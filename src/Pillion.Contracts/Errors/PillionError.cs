using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A structured error that can cross the RPC boundary.</summary>
[DataContract]
public sealed class PillionError
{
    /// <summary>Gets or sets the error code.</summary>
    [DataMember(Order = 0)]
    public PillionErrorCode Code { get; set; }

    /// <summary>Gets or sets a message suitable for showing to the user.</summary>
    [DataMember(Order = 1)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional hint describing whether and when to retry.</summary>
    [DataMember(Order = 2)]
    public string? RetryHint { get; set; }

    /// <summary>Gets or sets optional diagnostic details (never shown without redaction).</summary>
    [DataMember(Order = 3)]
    public string? Details { get; set; }
}
