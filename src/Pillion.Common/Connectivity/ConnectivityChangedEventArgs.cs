namespace Pillion.Common.Connectivity;

/// <summary>Data for <see cref="IConnectivityMonitor.StateChanged"/>.</summary>
public sealed class ConnectivityChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="ConnectivityChangedEventArgs"/> class.</summary>
    /// <param name="oldState">The state before the change.</param>
    /// <param name="newState">The state after the change.</param>
    /// <param name="reason">Why the state changed, for example <c>openai unreachable: timeout</c>.</param>
    /// <param name="snapshot">The per-provider reachability at the time of the change.</param>
    public ConnectivityChangedEventArgs(
        ConnectivityState oldState,
        ConnectivityState newState,
        string reason,
        IReadOnlyDictionary<string, ProviderReachability> snapshot)
    {
        OldState = oldState;
        NewState = newState;
        Reason = reason;
        Snapshot = snapshot;
    }

    /// <summary>Gets the state before the change.</summary>
    public ConnectivityState OldState { get; }

    /// <summary>Gets the state after the change.</summary>
    public ConnectivityState NewState { get; }

    /// <summary>Gets why the state changed.</summary>
    public string Reason { get; }

    /// <summary>Gets the per-provider reachability (cloud providers only) at the time of the change.</summary>
    public IReadOnlyDictionary<string, ProviderReachability> Snapshot { get; }
}
