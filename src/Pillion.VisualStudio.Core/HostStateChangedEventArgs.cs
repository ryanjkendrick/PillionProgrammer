using System;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>Describes a change of <see cref="IHostConnection.State"/>.</summary>
public sealed class HostStateChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="HostStateChangedEventArgs"/> class.</summary>
    /// <param name="previousState">The state before the change.</param>
    /// <param name="state">The new state.</param>
    /// <param name="restartAttempt">The number of crashes in the current crash window, when restarting; otherwise 0.</param>
    /// <param name="maxRestartAttempts">The number of crashes in the window that makes the supervisor give up.</param>
    /// <param name="reason">Why the state changed, when known (always set for <see cref="HostState.Failed"/>).</param>
    public HostStateChangedEventArgs(HostState previousState, HostState state, int restartAttempt, int maxRestartAttempts, string? reason)
    {
        PreviousState = previousState;
        State = state;
        RestartAttempt = restartAttempt;
        MaxRestartAttempts = maxRestartAttempts;
        Reason = reason;
    }

    /// <summary>Gets the state before the change.</summary>
    public HostState PreviousState { get; }

    /// <summary>Gets the new state.</summary>
    public HostState State { get; }

    /// <summary>Gets the number of crashes in the current five-minute window while restarting; otherwise 0.</summary>
    public int RestartAttempt { get; }

    /// <summary>Gets the number of crashes within the window after which the supervisor gives up.</summary>
    public int MaxRestartAttempts { get; }

    /// <summary>Gets why the state changed, when known. Always set for <see cref="HostState.Failed"/>.</summary>
    public string? Reason { get; }
}
