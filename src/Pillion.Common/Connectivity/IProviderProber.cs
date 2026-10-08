namespace Pillion.Common.Connectivity;

/// <summary>What to probe: a configured cloud provider and the origin (scheme, host, port) of its endpoint.</summary>
/// <param name="ProviderId">The provider id from settings.</param>
/// <param name="Origin">The origin of the provider's endpoint. The path is always <c>/</c>.</param>
public sealed record ProbeTarget(string ProviderId, Uri Origin);

/// <summary>The outcome of one probe.</summary>
/// <param name="Reachable">Whether the origin answered at all (any HTTP status counts).</param>
/// <param name="ErrorCategory">Why it did not answer, or <see cref="ProbeErrorCategory.None"/>.</param>
/// <param name="Latency">How long the probe took, when known.</param>
public sealed record ProbeResult(bool Reachable, ProbeErrorCategory ErrorCategory, TimeSpan? Latency = null)
{
    /// <summary>Creates a result for an origin that answered.</summary>
    /// <param name="latency">How long the probe took, when known.</param>
    /// <returns>A reachable result.</returns>
    public static ProbeResult Success(TimeSpan? latency = null) => new(true, ProbeErrorCategory.None, latency);

    /// <summary>Creates a result for an origin that did not answer.</summary>
    /// <param name="category">Why not.</param>
    /// <returns>An unreachable result.</returns>
    public static ProbeResult Failure(ProbeErrorCategory category) => new(false, category);
}

/// <summary>Checks whether a provider's origin can be reached. Implementations must not throw for network failures.</summary>
public interface IProviderProber
{
    /// <summary>Probes one origin.</summary>
    /// <param name="target">The provider and origin to probe.</param>
    /// <param name="cancellationToken">Cancelled by the monitor when the probe takes longer than the probe timeout or the monitor stops.</param>
    /// <returns>The outcome.</returns>
    Task<ProbeResult> ProbeAsync(ProbeTarget target, CancellationToken cancellationToken);
}
