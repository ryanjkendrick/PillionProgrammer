using System;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using StreamJsonRpc;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// One host process plus its StreamJsonRpc connection: <see cref="LengthHeaderMessageHandler"/> over the named pipe
/// with a default <see cref="MessagePackFormatter"/>, the VSIX's local targets, the <c>hostStateChanged</c>
/// notification and typed proxies created with <see cref="JsonRpc.Attach{T}()"/>.
/// </summary>
/// <remarks>
/// The launch protocol originally named <see cref="HeaderDelimitedMessageHandler"/>, but in StreamJsonRpc 2.22 that
/// handler only works with text formatters: its <c>Write</c> reads the text encoding and throws
/// <see cref="System.NotSupportedException"/> for <see cref="MessagePackFormatter"/>. Pillion Host uses
/// <see cref="LengthHeaderMessageHandler"/> (the handler StreamJsonRpc pairs with MessagePack) for the same reason.
/// </remarks>
internal sealed class HostRpcSession : IHostSession
{
    /// <summary>The JSON-RPC notification the host sends when its state changes.</summary>
    public const string HostStateChangedMethod = "hostStateChanged";

    /// <summary>The JSON-RPC notification the host sends with its <see cref="ConnectivityState"/> after the handshake and on every change.</summary>
    public const string ConnectivityChangedMethod = "connectivityChanged";

    private static readonly TimeSpan ExitCodeGrace = TimeSpan.FromMilliseconds(500);

    private readonly HostProcess _process;
    private readonly HostJob? _job;
    private readonly NamedPipeClientStream _pipe;
    private readonly JsonRpc _rpc;
    private readonly PillionLog _log;
    private readonly string _token;
    private readonly ConcurrentDictionary<Type, object> _proxies = new();
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _hostStateNotified;
    private int _latestHostState = (int)HostState.Starting;
    private int _latestConnectivity = -1;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="HostRpcSession"/> class and starts listening.</summary>
    /// <param name="process">The host process (owned by the session from now on).</param>
    /// <param name="job">The kill-on-close job the process is in, if any (owned).</param>
    /// <param name="pipe">The connected, verified pipe (owned).</param>
    /// <param name="localTargets">The services exposed to the host.</param>
    /// <param name="log">The logger.</param>
    /// <param name="token">The protocol token, unregistered from the log's secrets on dispose.</param>
    public HostRpcSession(HostProcess process, HostJob? job, NamedPipeClientStream pipe, HostLocalTargets localTargets, PillionLog log, string token)
    {
        _process = process;
        _job = job;
        _pipe = pipe;
        _log = log;
        _token = token;

        _rpc = new PillionClientJsonRpc(CreateMessageHandler(pipe));
        _rpc.AddLocalRpcTarget<IEditorService>(localTargets.Editor, options: null);
        _rpc.AddLocalRpcTarget<IApprovalService>(localTargets.Approval, options: null);
        _rpc.AddLocalRpcTarget<IWorkspaceQueryService>(localTargets.WorkspaceQuery, options: null);
        _rpc.AddLocalRpcMethod(HostStateChangedMethod, new Action<HostState>(OnHostStateChanged));

        // Registered before the handshake: the host may send it before the handshake response arrives.
        _rpc.AddLocalRpcMethod(ConnectivityChangedMethod, new Action<ConnectivityState>(OnConnectivityChanged));
        _rpc.Disconnected += OnDisconnected;

        _ = _process.Exited.ContinueWith(
            static (exit, state) => ((HostRpcSession)state!).OnProcessExited(exit.Result),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _rpc.StartListening();
    }

    /// <inheritdoc/>
    public event EventHandler<HostState>? HostStateChanged;

    /// <inheritdoc/>
    public event EventHandler<ConnectivityState>? ConnectivityChanged;

    /// <summary>Creates the wire framing and serializer shared with Pillion Host.</summary>
    /// <param name="pipe">The connected pipe.</param>
    /// <returns>A <see cref="LengthHeaderMessageHandler"/> with a default <see cref="MessagePackFormatter"/>.</returns>
    public static IJsonRpcMessageHandler CreateMessageHandler(System.IO.Stream pipe) =>
        new LengthHeaderMessageHandler(pipe, pipe, new MessagePackFormatter());

    /// <inheritdoc/>
    public int ProcessId => _process.Id;

    /// <inheritdoc/>
    public HostState LatestHostState => (HostState)Volatile.Read(ref _latestHostState);

    /// <inheritdoc/>
    public ConnectivityState? LatestConnectivity
    {
        get
        {
            var value = Volatile.Read(ref _latestConnectivity);
            return value < 0 ? null : (ConnectivityState)value;
        }
    }

    /// <inheritdoc/>
    public Task<string> Completion => _completion.Task;

    /// <summary>Gets a value indicating whether the host process is in the session's kill-on-close job.</summary>
    public bool IsInJob => _job is not null && _job.Contains(_process);

    /// <inheritdoc/>
    public T GetProxy<T>()
        where T : class
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(HostRpcSession));
        }

        return (T)_proxies.GetOrAdd(typeof(T), _ => _rpc.Attach<T>());
    }

    /// <summary>Performs the handshake and reads the host's initial state.</summary>
    /// <param name="processId">The devenv.exe process id to report.</param>
    /// <param name="timeout">How long to wait for the host to answer.</param>
    /// <param name="cancellationToken">A token that cancels the handshake.</param>
    /// <returns>The host's handshake response (already checked for acceptance and compatibility).</returns>
    /// <exception cref="HostLaunchException">The host rejected the handshake, is incompatible or did not answer.</exception>
    public async Task<HandshakeResponse> HandshakeAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var lifecycle = GetProxy<IHostLifecycleService>();
        var request = new HandshakeRequest
        {
            ContractMajor = ContractVersion.Major,
            ContractMinor = ContractVersion.Minor,
            ClientProcessId = processId,
            ProtocolToken = _token,
        };

        HandshakeResponse response;
        using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutSource.CancelAfter(timeout);
            try
            {
                response = await lifecycle.HandshakeAsync(request, timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HostLaunchException($"Pillion Host did not answer the handshake within {timeout.TotalSeconds:0} s.");
            }
            catch (ConnectionLostException ex)
            {
                throw new HostLaunchException("The connection to Pillion Host was lost during the handshake.", ex);
            }
            catch (RemoteInvocationException ex)
            {
                throw new HostLaunchException("Pillion Host failed the handshake: " + ex.Message, ex);
            }
        }

        if (response is null)
        {
            throw new HostLaunchException("Pillion Host returned an empty handshake response.");
        }

        if (!response.Accepted)
        {
            throw new HostLaunchException(
                "Pillion Host rejected the connection: " + (string.IsNullOrEmpty(response.Reason) ? "no reason given" : response.Reason),
                isRetryable: false);
        }

        if (!ContractVersion.IsCompatible(response.ContractMajor, response.ContractMinor))
        {
            throw new HostLaunchException(
                $"Pillion Host speaks contract {response.ContractMajor}.{response.ContractMinor}, but this extension needs {ContractVersion.Major}.x. Reinstall the extension.",
                isRetryable: false);
        }

        if (response.HostProcessId != _process.Id)
        {
            _log.Warning("host.handshake.pid_mismatch", "The host reported a different process id than the one launched.", ("reported", response.HostProcessId), ("launched", _process.Id));
        }

        await SeedHostStateAsync(lifecycle, timeout, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc/>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        if (!_process.HasExited && !_completion.Task.IsCompleted)
        {
            try
            {
                using var timeoutSource = new CancellationTokenSource(timeout);
                await GetProxy<IHostLifecycleService>().ShutdownAsync(timeoutSource.Token).ConfigureAwait(false);
                _log.Info("host.shutdown.requested", "Asked Pillion Host to shut down.", ("pid", _process.Id));
            }
#pragma warning disable CA1031 // Any failure here just means we fall through to killing the process.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Info("host.shutdown.request_failed", "Pillion Host did not acknowledge shutdown.", ("pid", _process.Id), ("error", ex.GetType().Name));
            }
        }

        var remaining = timeout - (DateTime.UtcNow - started);
        if (remaining > TimeSpan.Zero)
        {
            _ = await _process.WaitForExitAsync(remaining).ConfigureAwait(false);
        }

        if (_process.HasExited)
        {
            _log.Info("host.shutdown.exited", "Pillion Host exited.", ("pid", _process.Id));
        }
        else
        {
            _log.Warning("host.shutdown.killed", "Pillion Host did not exit in time; killing its process tree.", ("pid", _process.Id), ("timeoutMs", (int)timeout.TotalMilliseconds));
            KillTree();
        }

        Dispose();
    }

    /// <summary>Kills the host and everything it started.</summary>
    public void KillTree()
    {
        if (_job is not null)
        {
            _job.Terminate();
        }
        else
        {
            _process.KillTree();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _rpc.Disconnected -= OnDisconnected;
        _rpc.Dispose();
        _pipe.Dispose();

        if (!_process.HasExited)
        {
            KillTree();
        }

        _job?.Dispose();
        _process.Dispose();
        _log.RemoveSecret(_token);
        _completion.TrySetResult("the extension closed the session");
    }

    private async Task SeedHostStateAsync(IHostLifecycleService lifecycle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var state = await lifecycle.GetStateAsync(timeoutSource.Token).ConfigureAwait(false);

            // A notification that arrived meanwhile is newer than this answer.
            if (Volatile.Read(ref _hostStateNotified) == 0)
            {
                Volatile.Write(ref _latestHostState, (int)state);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The host may not implement GetStateAsync yet; hostStateChanged still updates the state.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Warning("host.state.query_failed", "Could not read the host's initial state; waiting for hostStateChanged.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
    }

    private void OnHostStateChanged(HostState state)
    {
        Volatile.Write(ref _hostStateNotified, 1);
        Volatile.Write(ref _latestHostState, (int)state);
        _log.Info("host.state", "The host reported its state.", ("pid", _process.Id), ("state", state));
        HostStateChanged?.Invoke(this, state);
    }

    private void OnConnectivityChanged(ConnectivityState state)
    {
        Volatile.Write(ref _latestConnectivity, (int)state);
        _log.Info("host.connectivity", "The host reported its connectivity.", ("pid", _process.Id), ("connectivity", state));
        ConnectivityChanged?.Invoke(this, state);
    }

    private void OnDisconnected(object? sender, JsonRpcDisconnectedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // A crash usually shows up as a disconnect first; give the exit code a moment to arrive.
            if (await _process.WaitForExitAsync(ExitCodeGrace).ConfigureAwait(false))
            {
                OnProcessExited(_process.ExitCode ?? -1);
                return;
            }

            _log.Warning("host.disconnected", "The connection to Pillion Host was lost.", ("pid", _process.Id), ("reason", e.Reason), ("detail", e.Description));
            _completion.TrySetResult("the connection to Pillion Host was lost (" + e.Reason + ")");
        });
    }

    private void OnProcessExited(int exitCode)
    {
        if (_completion.TrySetResult("Pillion Host exited with code " + exitCode))
        {
            _log.Write(exitCode == 0 ? PillionLogLevel.Info : PillionLogLevel.Warning, "host.exited", "Pillion Host exited.", ("pid", _process.Id), ("exitCode", exitCode));
        }
    }
}
