using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The extension's connection to Pillion Host. One instance per devenv.exe, owned by the package and registered as a
/// Visual Studio service under this interface type. Consumers (chat window, options pages) use it to wait for the host
/// and to obtain RPC proxies; they never launch or talk to the host process directly.
/// </summary>
/// <remarks>
/// <see cref="StateChanged"/> is raised on a background thread. Switch to the main thread before touching UI.
/// </remarks>
[Guid("5d0c3f2e-7f4a-4a4e-9a8e-2f3b6f1d9c41")]
public interface IHostConnection
{
    /// <summary>Gets the current state of the host, as mirrored from the host and the supervisor.</summary>
    HostState State { get; }

    /// <summary>Raised on a background thread whenever <see cref="State"/> (or the restart attempt) changes.</summary>
    event EventHandler<HostStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Gets the RPC proxy for a host service interface (for example <see cref="IModelService"/>) on the live connection.
    /// </summary>
    /// <typeparam name="T">A host service interface from Pillion.Contracts.</typeparam>
    /// <returns>The proxy. Do not cache it across restarts; ask again after <see cref="StateChanged"/>.</returns>
    /// <exception cref="PillionRpcException">
    /// No handshaken connection exists (the host is starting, restarting or failed); the error code is
    /// <see cref="PillionErrorCode.HostRestarting"/>.
    /// </exception>
    T GetProxy<T>()
        where T : class;

    /// <summary>
    /// Waits until the host is <see cref="HostState.Ready"/> or <see cref="HostState.Degraded"/> with a live
    /// connection. Completes immediately if it already is. Keeps waiting through <see cref="HostState.Failed"/>,
    /// because the user can retry; pass a token to give up, and watch <see cref="StateChanged"/> to show failures.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>A task that completes when the host is usable.</returns>
    Task WhenReadyAsync(CancellationToken cancellationToken);
}
