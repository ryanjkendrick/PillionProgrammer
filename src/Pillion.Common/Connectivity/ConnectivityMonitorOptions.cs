namespace Pillion.Common.Connectivity;

/// <summary>Timing and thresholds for <see cref="ConnectivityMonitor"/>. The defaults follow the design document (section 11).</summary>
public sealed class ConnectivityMonitorOptions
{
    /// <summary>Gets or sets how often providers are probed. The default is 60 seconds.</summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets or sets how long one probe may take before the provider counts as unreachable. The default is 2 seconds.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets how long network-change events are coalesced before a probe round starts. The default is 1 second.</summary>
    public TimeSpan NetworkChangeDebounce { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets how many consecutive failure reports mark a provider unreachable. The default is 3.</summary>
    public int FailureThreshold { get; set; } = 3;
}
