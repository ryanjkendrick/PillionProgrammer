using System.Globalization;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>Formats the host state for the Visual Studio status bar.</summary>
public static class HostStatusText
{
    /// <summary>Where the user finds the logs, as named in the Tools menu.</summary>
    public const string OpenLogsHint = "see Tools > Pillion: Open Logs";

    /// <summary>Formats a state change as status bar text, for example "Pillion: Restarting (2/3)".</summary>
    /// <param name="change">The state change.</param>
    /// <returns>The status bar text.</returns>
    public static string Format(HostStateChangedEventArgs change)
    {
        if (change is null)
        {
            throw new System.ArgumentNullException(nameof(change));
        }

        return Format(change.State, change.RestartAttempt, change.MaxRestartAttempts);
    }

    /// <summary>
    /// Formats the host state and its connectivity as status bar text, for example "Pillion: Ready, Online" or
    /// "Pillion: Ready, Local-only". The connectivity is shown only while the host is Ready or Degraded.
    /// </summary>
    /// <param name="state">The host state.</param>
    /// <param name="restartAttempt">The restart attempt, or 0 when not counting.</param>
    /// <param name="maxRestartAttempts">The crash limit shown after the attempt.</param>
    /// <param name="connectivity">The host's connectivity, or <see langword="null"/> when unknown.</param>
    /// <returns>The status bar text.</returns>
    public static string Format(HostState state, int restartAttempt, int maxRestartAttempts, ConnectivityState? connectivity)
    {
        var text = Format(state, restartAttempt, maxRestartAttempts);
        return connectivity is { } value && state is HostState.Ready or HostState.Degraded
            ? text + ", " + ConnectivityName(value)
            : text;
    }

    /// <summary>Names a connectivity state for the status bar and the chat window: Online, Degraded, Offline, Local-only.</summary>
    /// <param name="connectivity">The connectivity state.</param>
    /// <returns>The name.</returns>
    public static string ConnectivityName(ConnectivityState connectivity) => connectivity switch
    {
        ConnectivityState.Online => "Online",
        ConnectivityState.Degraded => "Degraded",
        ConnectivityState.Offline => "Offline",
        ConnectivityState.LocalOnly => "Local-only",
        _ => connectivity.ToString(),
    };

    /// <summary>Formats a state as status bar text.</summary>
    /// <param name="state">The host state.</param>
    /// <param name="restartAttempt">The restart attempt, or 0 when not counting.</param>
    /// <param name="maxRestartAttempts">The crash limit shown after the attempt.</param>
    /// <returns>The status bar text.</returns>
    public static string Format(HostState state, int restartAttempt, int maxRestartAttempts) => state switch
    {
        HostState.Starting => "Pillion: Starting",
        HostState.Ready => "Pillion: Ready",
        HostState.Degraded => "Pillion: Degraded",
        HostState.Restarting when restartAttempt > 0 && maxRestartAttempts > 0 => string.Format(
            CultureInfo.InvariantCulture, "Pillion: Restarting ({0}/{1})", restartAttempt, maxRestartAttempts),
        HostState.Restarting => "Pillion: Restarting",
        HostState.Failed => "Pillion: Failed, " + OpenLogsHint,
        _ => "Pillion: " + state.ToString(),
    };
}
