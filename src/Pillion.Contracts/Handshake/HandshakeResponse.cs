using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The host's reply to a <see cref="HandshakeRequest"/>.</summary>
[DataContract]
public sealed class HandshakeResponse
{
    /// <summary>Gets or sets the host's <see cref="ContractVersion.Major"/>.</summary>
    [DataMember(Order = 0)]
    public int ContractMajor { get; set; }

    /// <summary>Gets or sets the host's <see cref="ContractVersion.Minor"/>.</summary>
    [DataMember(Order = 1)]
    public int ContractMinor { get; set; }

    /// <summary>Gets or sets the host's product version.</summary>
    [DataMember(Order = 2)]
    public string HostVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the host's process id.</summary>
    [DataMember(Order = 3)]
    public int HostProcessId { get; set; }

    /// <summary>Gets or sets a value indicating whether the host accepted the connection.</summary>
    [DataMember(Order = 4)]
    public bool Accepted { get; set; }

    /// <summary>Gets or sets the reason the connection was rejected, when <see cref="Accepted"/> is false.</summary>
    [DataMember(Order = 5)]
    public string? Reason { get; set; }
}
