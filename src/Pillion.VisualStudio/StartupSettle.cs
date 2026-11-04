using System;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio;

/// <summary>
/// Says when the package's start-up is over, for work that should not compete with it: the host has settled (Ready,
/// Degraded or Failed), or a timeout passed, plus a short grace period. The Output pane is created by Visual Studio on
/// first use, which costs tens of milliseconds of UI-thread time (more on 17.x); a line that must be shown during the
/// load waits for this, so the creation stays out of the folder-open critical path. (The Error List and the Output
/// window reader are connected on their first use instead, issue #49.)
/// </summary>
internal static class StartupSettle
{
    /// <summary>Completes when the host has settled or <paramref name="timeout"/> passed, then after <paramref name="grace"/> more.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="timeout">The longest to wait for the host.</param>
    /// <param name="grace">How long to wait after that.</param>
    /// <param name="cancellationToken">Cancelled when Visual Studio shuts down.</param>
    /// <returns>A task that completes when start-up is over (and is cancelled with the token).</returns>
    public static async Task WhenSettledAsync(IHostConnection connection, TimeSpan timeout, TimeSpan grace, CancellationToken cancellationToken)
    {
        var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(object? sender, HostStateChangedEventArgs e)
        {
            if (IsSettled(e.State))
            {
                settled.TrySetResult(true);
            }
        }

        connection.StateChanged += OnChanged;
        try
        {
            if (IsSettled(connection.State))
            {
                settled.TrySetResult(true);
            }

            _ = await Task.WhenAny(settled.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            connection.StateChanged -= OnChanged;
        }

        await Task.Delay(grace, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSettled(HostState state) => state is HostState.Ready or HostState.Degraded or HostState.Failed;
}
