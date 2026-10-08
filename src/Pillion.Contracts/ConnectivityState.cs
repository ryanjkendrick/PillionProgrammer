namespace Pillion.Contracts;

/// <summary>
/// How well Pillion Host can reach the cloud model providers the user has configured. The host sends the current
/// value to the extension in the <c>connectivityChanged</c> notification (an integer on the wire).
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
