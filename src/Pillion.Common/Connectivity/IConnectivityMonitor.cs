namespace Pillion.Common.Connectivity;

/// <summary>
/// Tracks whether the cloud model providers the user has configured can be reached. Thread-safe.
/// </summary>
public interface IConnectivityMonitor
{
    /// <summary>
    /// Raised once for every change of <see cref="State"/>, outside the monitor's locks and in the order the changes
    /// happened. Handlers run on the thread that caused the change (a timer, a probe or a caller) and must not block.
    /// </summary>
    event EventHandler<ConnectivityChangedEventArgs>? StateChanged;

    /// <summary>Gets the current state.</summary>
    ConnectivityState State { get; }

    /// <summary>
    /// Gets what is known about each monitored provider (enabled cloud providers, keyed by provider id,
    /// case-insensitive). Empty in <see cref="ConnectivityState.LocalOnly"/>, because nothing is probed then.
    /// </summary>
    IReadOnlyDictionary<string, ProviderReachability> Snapshot { get; }

    /// <summary>Determines whether a provider can be used right now.</summary>
    /// <param name="providerId">The provider id from settings.</param>
    /// <returns>
    /// <see langword="true"/> for a local provider (it is never probed) and for a cloud provider that is reachable;
    /// <see langword="false"/> for an unreachable cloud provider, for any cloud provider in
    /// <see cref="ConnectivityState.LocalOnly"/>, and for a provider that is not configured and enabled.
    /// </returns>
    bool IsProviderReachable(string providerId);

    /// <summary>Probes every monitored provider now and waits for the results. Does nothing in <see cref="ConnectivityState.LocalOnly"/>.</summary>
    /// <param name="cancellationToken">Cancels the wait (not the probes themselves).</param>
    /// <returns>A task that completes when the probe round has finished.</returns>
    Task ProbeNowAsync(CancellationToken cancellationToken);

    /// <summary>Reports that a request to a provider failed (circuit-breaker evidence). Three in a row mark the provider unreachable.</summary>
    /// <param name="providerId">The provider id.</param>
    void ReportFailure(string providerId);

    /// <summary>Reports that a request to a provider succeeded. Clears the failure count and any breaker mark.</summary>
    /// <param name="providerId">The provider id.</param>
    void ReportSuccess(string providerId);
}
