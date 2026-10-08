using Pillion.Contracts;
using StreamJsonRpc;

namespace Pillion.Host.Rpc;

/// <summary>
/// Host-to-VSIX calls for later host components (agent, tools). Proxies are attached to the current authenticated
/// connection; when no VSIX is connected the getters throw a <see cref="PillionRpcException"/> with
/// <see cref="PillionErrorCode.HostRestarting"/>.
/// </summary>
/// <remarks>
/// Get a proxy for each operation rather than caching it: after a reconnect the old proxy belongs to a closed
/// connection and its calls fail with <see cref="ConnectionLostException"/>. Never dispose a proxy; that would
/// close the connection.
/// </remarks>
internal interface IVsixConnection
{
    /// <summary>Raised when a VSIX connects (handshake accepted) or disconnects.</summary>
    event EventHandler? ConnectionChanged;

    /// <summary>Gets a value indicating whether an authenticated VSIX connection exists.</summary>
    bool IsConnected { get; }

    /// <summary>Gets the editor service on the current connection.</summary>
    /// <returns>A proxy for <see cref="IEditorService"/>.</returns>
    /// <exception cref="PillionRpcException">No VSIX is connected (<see cref="PillionErrorCode.HostRestarting"/>).</exception>
    IEditorService GetEditorService();

    /// <summary>Gets the approval service on the current connection.</summary>
    /// <returns>A proxy for <see cref="IApprovalService"/>.</returns>
    /// <exception cref="PillionRpcException">No VSIX is connected (<see cref="PillionErrorCode.HostRestarting"/>).</exception>
    IApprovalService GetApprovalService();

    /// <summary>Gets the workspace query service on the current connection.</summary>
    /// <returns>A proxy for <see cref="IWorkspaceQueryService"/>.</returns>
    /// <exception cref="PillionRpcException">No VSIX is connected (<see cref="PillionErrorCode.HostRestarting"/>).</exception>
    IWorkspaceQueryService GetWorkspaceQueryService();
}

/// <summary>The <see cref="IVsixConnection"/> the RPC server keeps up to date.</summary>
internal sealed class VsixConnection : IVsixConnection
{
    private readonly Lock _lock = new();
    private Proxies? _current;

    /// <inheritdoc/>
    public event EventHandler? ConnectionChanged;

    /// <inheritdoc/>
    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _current is not null;
            }
        }
    }

    /// <inheritdoc/>
    public IEditorService GetEditorService() => GetCurrent().Editor.Value;

    /// <inheritdoc/>
    public IApprovalService GetApprovalService() => GetCurrent().Approval.Value;

    /// <inheritdoc/>
    public IWorkspaceQueryService GetWorkspaceQueryService() => GetCurrent().Workspace.Value;

    /// <summary>Makes <paramref name="rpc"/> the current connection (after its handshake is accepted).</summary>
    /// <param name="rpc">The connection's JSON-RPC instance.</param>
    public void Attach(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        lock (_lock)
        {
            _current = new Proxies(rpc);
        }

        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears the current connection if it is <paramref name="rpc"/>.</summary>
    /// <param name="rpc">The connection's JSON-RPC instance.</param>
    public void Detach(JsonRpc rpc)
    {
        lock (_lock)
        {
            if (_current is null || !ReferenceEquals(_current.Rpc, rpc))
            {
                return;
            }

            _current = null;
        }

        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private Proxies GetCurrent()
    {
        lock (_lock)
        {
            return _current is { Rpc.IsDisposed: false } current ? current : throw RpcErrors.NotConnected();
        }
    }

    private sealed class Proxies(JsonRpc rpc)
    {
        public JsonRpc Rpc { get; } = rpc;

        public Lazy<IEditorService> Editor { get; } = new(rpc.Attach<IEditorService>);

        public Lazy<IApprovalService> Approval { get; } = new(rpc.Attach<IApprovalService>);

        public Lazy<IWorkspaceQueryService> Workspace { get; } = new(rpc.Attach<IWorkspaceQueryService>);
    }
}
