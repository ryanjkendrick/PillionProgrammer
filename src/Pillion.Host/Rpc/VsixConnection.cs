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

    /// <summary>Gets what the connected VSIX said about itself in the handshake (contract 1.4), or <see langword="null"/> when none is connected.</summary>
    VsixClientInfo? Client { get; }

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

    /// <summary>Gets the workspace trust service on the current connection (contract 1.4; a 1.3 VSIX answers method-not-found).</summary>
    /// <returns>A proxy for <see cref="ITrustService"/>.</returns>
    /// <exception cref="PillionRpcException">No VSIX is connected (<see cref="PillionErrorCode.HostRestarting"/>).</exception>
    ITrustService GetTrustService();
}

/// <summary>What the connected VSIX reported in its <see cref="HandshakeRequest"/>.</summary>
/// <param name="ProcessId">The devenv.exe process id.</param>
/// <param name="ContractMajor">The client's contract major version.</param>
/// <param name="ContractMinor">The client's contract minor version (a 1.3 client calls none of the 1.4 methods and handles none of its notifications).</param>
/// <param name="VisualStudioInstallationPath">The launching Visual Studio's installation path (contract 1.4), or empty from a 1.3 client; the Command Runner (work item 3.11) loads its Developer PowerShell environment.</param>
/// <param name="VisualStudioVersion">The launching Visual Studio's version, or empty.</param>
internal sealed record VsixClientInfo(int ProcessId, int ContractMajor, int ContractMinor, string VisualStudioInstallationPath, string VisualStudioVersion)
{
    /// <summary>Creates the record from an accepted handshake request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The record.</returns>
    public static VsixClientInfo From(HandshakeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new VsixClientInfo(
            request.ClientProcessId,
            request.ContractMajor,
            request.ContractMinor,
            request.VisualStudioInstallationPath ?? string.Empty,
            request.VisualStudioVersion ?? string.Empty);
    }
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
    public VsixClientInfo? Client
    {
        get
        {
            lock (_lock)
            {
                return _current?.Client;
            }
        }
    }

    /// <inheritdoc/>
    public IEditorService GetEditorService() => GetCurrent().Editor.Value;

    /// <inheritdoc/>
    public IApprovalService GetApprovalService() => GetCurrent().Approval.Value;

    /// <inheritdoc/>
    public IWorkspaceQueryService GetWorkspaceQueryService() => GetCurrent().Workspace.Value;

    /// <inheritdoc/>
    public ITrustService GetTrustService() => GetCurrent().Trust.Value;

    /// <summary>Makes <paramref name="rpc"/> the current connection (after its handshake is accepted).</summary>
    /// <param name="rpc">The connection's JSON-RPC instance.</param>
    /// <param name="client">What the client said in the handshake, or <see langword="null"/> when not known.</param>
    public void Attach(JsonRpc rpc, VsixClientInfo? client = null)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        lock (_lock)
        {
            _current = new Proxies(rpc, client);
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

    /// <summary>Sends a JSON-RPC notification with one positional argument to the current connection, if there is one.</summary>
    /// <param name="method">The notification (method) name.</param>
    /// <param name="argument">The single positional argument.</param>
    /// <returns><see langword="true"/> when the notification was written; <see langword="false"/> when no VSIX is connected or the connection closed.</returns>
    public async Task<bool> TryNotifyAsync(string method, object? argument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        JsonRpc rpc;
        lock (_lock)
        {
            if (_current is not { Rpc.IsDisposed: false } current)
            {
                return false;
            }

            rpc = current.Rpc;
        }

        try
        {
            await rpc.NotifyAsync(method, argument).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private Proxies GetCurrent()
    {
        lock (_lock)
        {
            return _current is { Rpc.IsDisposed: false } current ? current : throw RpcErrors.NotConnected();
        }
    }

    private sealed class Proxies(JsonRpc rpc, VsixClientInfo? client)
    {
        public JsonRpc Rpc { get; } = rpc;

        public VsixClientInfo? Client { get; } = client;

        public Lazy<IEditorService> Editor { get; } = new(rpc.Attach<IEditorService>);

        public Lazy<IApprovalService> Approval { get; } = new(rpc.Attach<IApprovalService>);

        public Lazy<IWorkspaceQueryService> Workspace { get; } = new(rpc.Attach<IWorkspaceQueryService>);

        public Lazy<ITrustService> Trust { get; } = new(rpc.Attach<ITrustService>);
    }
}
