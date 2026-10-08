using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Host lifecycle, implemented by the host and called by the VSIX.</summary>
public interface IHostLifecycleService
{
    /// <summary>Performs the version and token handshake. Must be the first call on a connection.</summary>
    /// <param name="request">The client's handshake.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The host's handshake.</returns>
    Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken cancellationToken);

    /// <summary>Gets the host's lifecycle state.</summary>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The host state.</returns>
    Task<HostState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Asks the host to shut down cleanly.</summary>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when shutdown has started.</returns>
    Task ShutdownAsync(CancellationToken cancellationToken);
}
