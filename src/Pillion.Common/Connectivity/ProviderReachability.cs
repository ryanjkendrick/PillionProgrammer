namespace Pillion.Common.Connectivity;

/// <summary>What the monitor currently knows about one cloud provider.</summary>
/// <param name="Reachable">Whether the provider is considered reachable. A provider that has not been probed yet is assumed reachable.</param>
/// <param name="LastProbeTime">When the provider was last probed, or <see langword="null"/> if it has not been probed.</param>
/// <param name="ErrorCategory">Why the provider is unreachable, or <see cref="ProbeErrorCategory.None"/>.</param>
/// <param name="Latency">How long the last successful probe took, or <see langword="null"/>.</param>
public sealed record ProviderReachability(
    bool Reachable,
    DateTimeOffset? LastProbeTime,
    ProbeErrorCategory ErrorCategory,
    TimeSpan? Latency)
{
    /// <summary>Gets the state of a provider that has not been probed yet.</summary>
    public static ProviderReachability Unknown { get; } = new(true, null, ProbeErrorCategory.None, null);
}
