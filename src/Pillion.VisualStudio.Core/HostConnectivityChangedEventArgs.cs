using System;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>Describes a change of <see cref="IHostConnection.Connectivity"/>.</summary>
public sealed class HostConnectivityChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="HostConnectivityChangedEventArgs"/> class.</summary>
    /// <param name="previous">The connectivity before the change, or <see langword="null"/> when it was unknown.</param>
    /// <param name="current">The new connectivity, or <see langword="null"/> when no host is connected.</param>
    public HostConnectivityChangedEventArgs(ConnectivityState? previous, ConnectivityState? current)
    {
        Previous = previous;
        Current = current;
    }

    /// <summary>Gets the connectivity before the change, or <see langword="null"/> when it was unknown.</summary>
    public ConnectivityState? Previous { get; }

    /// <summary>Gets the new connectivity, or <see langword="null"/> when no host is connected (or it has not reported yet).</summary>
    public ConnectivityState? Current { get; }
}
