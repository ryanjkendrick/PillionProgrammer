using System.Reflection;
using Pillion.Contracts;

namespace Pillion.Host;

/// <summary>Version information about the running host.</summary>
internal static class HostInfo
{
    /// <summary>Gets the host's product version (the assembly informational version).</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Gets the text printed by <c>--version</c>.</summary>
    public static string VersionText => $"Pillion Host {Version} (contract {ContractVersion.Text})";

    private static string ReadVersion()
    {
        var assembly = typeof(HostInfo).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
