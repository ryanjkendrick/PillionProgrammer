using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Sent by the VSIX as the first call on a new connection.</summary>
[DataContract]
public sealed class HandshakeRequest
{
    /// <summary>Gets or sets the client's <see cref="ContractVersion.Major"/>.</summary>
    [DataMember(Order = 0)]
    public int ContractMajor { get; set; }

    /// <summary>Gets or sets the client's <see cref="ContractVersion.Minor"/>.</summary>
    [DataMember(Order = 1)]
    public int ContractMinor { get; set; }

    /// <summary>Gets or sets the process id of the client (devenv.exe).</summary>
    [DataMember(Order = 2)]
    public int ClientProcessId { get; set; }

    /// <summary>Gets or sets the per-launch secret token the client passed to the host when starting it.</summary>
    [DataMember(Order = 3)]
    public string ProtocolToken { get; set; } = string.Empty;
}
