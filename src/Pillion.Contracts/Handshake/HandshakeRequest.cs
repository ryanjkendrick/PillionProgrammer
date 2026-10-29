using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Sent by the VSIX as the first call on a new connection. Members from <see cref="VisualStudioInstallationPath"/> on were added in contract 1.4 (work item 3.5).</summary>
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

    /// <summary>
    /// Gets or sets the installation path of the Visual Studio that launched the host (the folder holding <c>Common7\IDE</c>),
    /// so the Command Runner can load its Developer PowerShell environment (work item 3.11). Empty from a 1.3 client; the host
    /// then uses the newest installation it finds. Added in 1.4.
    /// </summary>
    [DataMember(Order = 4)]
    public string VisualStudioInstallationPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the launching Visual Studio's version (for example <c>17.14.36221.1</c>), for the logs and the Collect diagnostics bundle. Empty from a 1.3 client. Added in 1.4.</summary>
    [DataMember(Order = 5)]
    public string VisualStudioVersion { get; set; } = string.Empty;
}
