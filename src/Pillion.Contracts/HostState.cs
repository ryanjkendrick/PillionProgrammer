namespace Pillion.Contracts;

/// <summary>The lifecycle state of the Pillion Host process.</summary>
public enum HostState
{
    /// <summary>The host is starting and not yet ready to serve requests.</summary>
    Starting,

    /// <summary>The host is ready to serve requests.</summary>
    Ready,

    /// <summary>The host is running but some features (for example a provider) are unavailable.</summary>
    Degraded,

    /// <summary>The host is being restarted by the supervisor.</summary>
    Restarting,

    /// <summary>The host has failed and will not be restarted automatically.</summary>
    Failed,
}
