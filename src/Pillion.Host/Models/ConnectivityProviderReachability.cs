using Pillion.Common.Connectivity;
using Pillion.ModelGateway;

namespace Pillion.Host.Models;

/// <summary>
/// The Model Gateway's <see cref="IProviderReachability"/> over the connectivity monitor (design section 11). The router
/// skips a cloud provider the monitor has found unreachable (by probe or by three failures reported from the circuit
/// breakers), and every cloud provider in Local-only mode.
/// </summary>
/// <remarks>
/// A provider the monitor does not track (a local provider, a cloud provider without a usable endpoint, or one added
/// to the settings a moment ago that the monitor has not picked up yet) counts as reachable, as an unprobed provider
/// does in the monitor itself. Before the monitor has started its state is Online with no providers tracked, so a
/// request that arrives during start-up is not refused. Local-only is decided from the same effective settings the
/// router reads (see <see cref="ConnectivityGatewaySettingsSource"/>), so the router's own policy check and this one agree.
/// </remarks>
internal sealed class ConnectivityProviderReachability : IProviderReachability
{
    private readonly IConnectivityMonitor _monitor;

    /// <summary>Initializes a new instance of the <see cref="ConnectivityProviderReachability"/> class.</summary>
    /// <param name="monitor">The connectivity monitor.</param>
    public ConnectivityProviderReachability(IConnectivityMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
    }

    /// <inheritdoc/>
    public bool IsReachable(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);

        if (_monitor.State == ConnectivityState.LocalOnly)
        {
            // The monitor answers true only for the providers it classifies as local.
            return _monitor.IsProviderReachable(providerId);
        }

        return !_monitor.Snapshot.TryGetValue(providerId, out var reachability) || reachability.Reachable;
    }
}
