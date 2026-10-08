namespace Pillion.Common.Connectivity;

/// <summary>
/// How well the host can reach the configured cloud model providers. The members and their numeric values match
/// <see cref="Pillion.Contracts.ConnectivityState"/>, which carries the state to the extension.
/// </summary>
public enum ConnectivityState
{
    /// <summary>Every configured cloud provider is reachable, or none is configured.</summary>
    Online,

    /// <summary>Some, but not all, configured cloud providers are unreachable.</summary>
    Degraded,

    /// <summary>No configured cloud provider is reachable.</summary>
    Offline,

    /// <summary>Local-only mode is on (set by the user or a repository policy); nothing leaves the machine and no probes run.</summary>
    LocalOnly,
}
