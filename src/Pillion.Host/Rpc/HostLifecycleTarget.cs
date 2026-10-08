using Pillion.Contracts;
using Pillion.Host.Lifecycle;

namespace Pillion.Host.Rpc;

/// <summary>The per-connection implementation of <see cref="IHostLifecycleService"/>.</summary>
internal sealed class HostLifecycleTarget : IHostLifecycleService
{
    private readonly RpcConnection _connection;
    private readonly HostStateMachine _state;
    private readonly HostShutdown _shutdown;

    /// <summary>Initializes a new instance of the <see cref="HostLifecycleTarget"/> class.</summary>
    /// <param name="connection">The connection this target serves.</param>
    /// <param name="state">The host state.</param>
    /// <param name="shutdown">Used by <see cref="ShutdownAsync"/>.</param>
    public HostLifecycleTarget(RpcConnection connection, HostStateMachine state, HostShutdown shutdown)
    {
        _connection = connection;
        _state = state;
        _shutdown = shutdown;
    }

    /// <inheritdoc/>
    public Task<HandshakeResponse> HandshakeAsync(HandshakeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(_connection.Handshake(request));

    /// <inheritdoc/>
    public Task<HostState> GetStateAsync(CancellationToken cancellationToken) => Task.FromResult(_state.Current);

    /// <inheritdoc/>
    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        // Returns before the stop runs so this call's response is sent; the server then drains in-flight calls.
        _shutdown.RequestStop(HostShutdown.Success, "shutdown requested by the client");
        return Task.CompletedTask;
    }
}
