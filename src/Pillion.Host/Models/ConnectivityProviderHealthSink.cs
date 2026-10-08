using Pillion.Common.Connectivity;
using Pillion.ModelGateway;

namespace Pillion.Host.Models;

/// <summary>
/// Feeds the Model Gateway's circuit-breaker evidence to the connectivity monitor (design section 11): each request
/// failure that counts against a provider is <see cref="IConnectivityMonitor.ReportFailure"/>, each success is
/// <see cref="IConnectivityMonitor.ReportSuccess"/>. Three failures in a row mark a cloud provider unreachable until
/// the next successful probe or request; the monitor ignores reports for providers it does not probe (local ones).
/// </summary>
internal sealed class ConnectivityProviderHealthSink : IProviderHealthSink
{
    private readonly IConnectivityMonitor _monitor;

    /// <summary>Initializes a new instance of the <see cref="ConnectivityProviderHealthSink"/> class.</summary>
    /// <param name="monitor">The connectivity monitor.</param>
    public ConnectivityProviderHealthSink(IConnectivityMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
    }

    /// <inheritdoc/>
    public void RequestFailed(string providerId) => _monitor.ReportFailure(providerId);

    /// <inheritdoc/>
    public void RequestSucceeded(string providerId) => _monitor.ReportSuccess(providerId);

    /// <inheritdoc/>
    public void CircuitOpened(string providerId, DateTimeOffset retryAt)
    {
        // The failures that opened the circuit have already been reported one by one.
    }

    /// <inheritdoc/>
    public void CircuitClosed(string providerId)
    {
        // The success that closed the circuit has already been reported.
    }
}
