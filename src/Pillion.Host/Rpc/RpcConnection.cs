using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using MessagePack;
using Microsoft.Extensions.Logging;
using Pillion.Contracts;
using Pillion.Host.Lifecycle;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Pillion.Host.Rpc;

/// <summary>Everything a connection needs from the server, resolved once at start-up.</summary>
/// <param name="Token">The launch token.</param>
/// <param name="Options">The host options (parent process id).</param>
/// <param name="ServerOptions">The RPC server timeouts.</param>
/// <param name="ClientProcessIds">Resolves the pipe client's process id.</param>
/// <param name="State">The host state machine.</param>
/// <param name="Shutdown">Requests a host stop.</param>
/// <param name="Vsix">The host-to-VSIX connection holder.</param>
/// <param name="Targets">The registered RPC targets.</param>
/// <param name="Services">The host's service provider (to resolve targets).</param>
/// <param name="LoggerFactory">Creates loggers.</param>
internal sealed record RpcConnectionContext(
    LaunchToken Token,
    PillionHostOptions Options,
    RpcServerOptions ServerOptions,
    IPipeClientProcessIdProvider ClientProcessIds,
    HostStateMachine State,
    HostShutdown Shutdown,
    VsixConnection Vsix,
    IReadOnlyList<IRpcTarget> Targets,
    IServiceProvider Services,
    ILoggerFactory LoggerFactory);

/// <summary>
/// One client connection: a StreamJsonRpc <see cref="JsonRpc"/> over length-prefixed MessagePack on the connected
/// pipe, the handshake state, <c>hostStateChanged</c> notifications and the host-to-VSIX proxies.
/// </summary>
internal sealed partial class RpcConnection : IAsyncDisposable
{
    /// <summary>The name of the notification sent to the client when the host state changes.</summary>
    public const string HostStateChangedNotification = "hostStateChanged";

    private static int s_nextId;

    private readonly NamedPipeServerStream _pipe;
    private readonly RpcConnectionContext _context;
    private readonly ILogger _logger;
    private readonly PillionJsonRpc _rpc;
    private readonly Lock _lock = new();
    private readonly Channel<HostState> _notifications = Channel.CreateUnbounded<HostState>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _handshakeTimeout = new();
    private readonly int? _clientProcessId;
    private HandshakeResponse? _accepted;
    private bool _rejected;
    private bool _subscribed;
    private Task _notificationPump = Task.CompletedTask;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="RpcConnection"/> class.</summary>
    /// <param name="pipe">The connected server pipe. The connection owns it.</param>
    /// <param name="context">The server's shared dependencies.</param>
    public RpcConnection(NamedPipeServerStream pipe, RpcConnectionContext context)
    {
        _pipe = pipe;
        _context = context;
        _logger = context.LoggerFactory.CreateLogger<RpcConnection>();
        Id = Interlocked.Increment(ref s_nextId);
        _clientProcessId = context.ClientProcessIds.GetClientProcessId(pipe);

        var formatter = new MessagePackFormatter();

        // Same wire format as the defaults; only hardens deserialisation of data from another process.
        formatter.SetMessagePackSerializerOptions(
            MessagePackFormatter.DefaultUserDataSerializationOptions.WithSecurity(MessagePackSecurity.UntrustedData));

        // MessagePack is binary, so frames are length-prefixed (4-byte big-endian length, then the message).
        // HeaderDelimitedMessageHandler only supports text formatters and throws NotSupportedException with MessagePack.
        var handler = new LengthHeaderMessageHandler(pipe, pipe, formatter);
        _rpc = new PillionJsonRpc(handler, () => IsAccepted, _logger)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            TraceSource = JsonRpcTraceListener.CreateTraceSource(_logger),
        };
        _rpc.ResponseSent += OnResponseSent;
    }

    /// <summary>Gets the connection's sequence number (for logs).</summary>
    public int Id { get; }

    /// <summary>Gets a value indicating whether the handshake has been accepted.</summary>
    public bool IsAccepted => Volatile.Read(ref _accepted) is not null;

    /// <summary>Gets a task that completes when the connection has closed.</summary>
    public Task Completion => _rpc.Completion;

    /// <summary>Registers the targets and starts reading requests.</summary>
    public void Start()
    {
        var targetOptions = new JsonRpcTargetOptions { NotifyClientOfEvents = false, DisposeOnDisconnect = false };
        _rpc.AddLocalRpcTarget(
            typeof(IHostLifecycleService),
            new HostLifecycleTarget(this, _context.State, _context.Shutdown),
            targetOptions);

        foreach (var target in _context.Targets)
        {
            _rpc.AddLocalRpcTarget(target.ContractType, target.GetTarget(_context.Services), targetOptions);
        }

        _rpc.StartListening();
        LogConnected(_logger, Id, _clientProcessId);

        _handshakeTimeout.Token.Register(OnHandshakeTimeout);
        _handshakeTimeout.CancelAfter(_context.ServerOptions.HandshakeTimeout);
    }

    /// <summary>Validates a handshake and, when accepted, authenticates the connection. Idempotent once accepted.</summary>
    /// <param name="request">The client's handshake.</param>
    /// <returns>The host's handshake response.</returns>
    public HandshakeResponse Handshake(HandshakeRequest request)
    {
        lock (_lock)
        {
            if (_accepted is not null)
            {
                LogRepeatHandshake(_logger, Id);
                return _accepted;
            }

            if (_rejected)
            {
                return CreateResponse(accepted: false, "The connection has already been rejected.");
            }
        }

        var outcome = HandshakeValidator.Validate(request, _context.Token, _context.Options.ParentProcessId, _clientProcessId);
        if (!outcome.Accepted)
        {
            lock (_lock)
            {
                _rejected = true;
            }

            LogRejected(_logger, Id, outcome.Check, outcome.Reason);

            // Normally closed as soon as the response has been sent (OnResponseSent); this is the fallback.
            _ = Task.Delay(_context.ServerOptions.RejectionCloseDelay).ContinueWith(
                _ => Close(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return CreateResponse(accepted: false, outcome.Reason);
        }

        HandshakeResponse response;
        lock (_lock)
        {
            if (_accepted is not null)
            {
                return _accepted;
            }

            response = CreateResponse(accepted: true, reason: null);
            Volatile.Write(ref _accepted, response);
            _context.State.StateChanged += OnStateChanged;
            _subscribed = true;
        }

        LogAccepted(_logger, Id, request.ContractMajor, request.ContractMinor);
        _notificationPump = PumpNotificationsAsync();
        _context.Vsix.Attach(_rpc);
        return response;
    }

    /// <summary>Waits until no call is in flight (or the connection closes), up to <paramref name="timeout"/>.</summary>
    /// <param name="timeout">The longest time to wait.</param>
    /// <returns>A task that completes when the connection is idle or the timeout has elapsed.</returns>
    public async Task DrainAsync(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!_rpc.IsIdle && !_rpc.Completion.IsCompleted && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        if (!_rpc.IsIdle && !_rpc.Completion.IsCompleted)
        {
            LogDrainTimedOut(_logger, Id);
        }
    }

    /// <summary>Closes the connection (in-flight calls are cancelled).</summary>
    public void Close() => _rpc.Dispose();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_subscribed)
            {
                _context.State.StateChanged -= OnStateChanged;
                _subscribed = false;
            }
        }

        _notifications.Writer.TryComplete();
        _context.Vsix.Detach(_rpc);
        _rpc.ResponseSent -= OnResponseSent;
        _rpc.Dispose();

        try
        {
            await _notificationPump.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or ConnectionLostException or IOException)
        {
            // Connection already gone.
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
        _handshakeTimeout.Dispose();
        LogClosed(_logger, Id);
    }

    private static HandshakeResponse CreateResponse(bool accepted, string? reason) => new()
    {
        ContractMajor = ContractVersion.Major,
        ContractMinor = ContractVersion.Minor,
        HostVersion = HostInfo.Version,
        HostProcessId = Environment.ProcessId,
        Accepted = accepted,
        Reason = reason,
    };

    private void OnHandshakeTimeout()
    {
        if (IsAccepted || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        LogHandshakeTimedOut(_logger, Id, _context.ServerOptions.HandshakeTimeout);
        Close();
    }

    private void OnResponseSent(object? sender, JsonRpcMessage message)
    {
        if (message is JsonRpcResult { Result: HandshakeResponse { Accepted: false } })
        {
            // The rejection has left the host; close the connection.
            _ = Task.Run(Close);
        }
    }

    private void OnStateChanged(object? sender, HostStateChangedEventArgs e) => _notifications.Writer.TryWrite(e.Current);

    private async Task PumpNotificationsAsync()
    {
        await foreach (var state in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _rpc.NotifyAsync(HostStateChangedNotification, state).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or ConnectionLostException or IOException or OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 210, Level = LogLevel.Information, Message = "Connection {ConnectionId} opened by process {ClientProcessId}.")]
    private static partial void LogConnected(ILogger logger, int connectionId, int? clientProcessId);

    [LoggerMessage(EventId = 211, Level = LogLevel.Information, Message = "Connection {ConnectionId} handshake accepted (client contract {Major}.{Minor}).")]
    private static partial void LogAccepted(ILogger logger, int connectionId, int major, int minor);

    [LoggerMessage(EventId = 212, Level = LogLevel.Warning, Message = "Connection {ConnectionId} handshake rejected by the {Check} check: {Reason}")]
    private static partial void LogRejected(ILogger logger, int connectionId, HandshakeCheck check, string? reason);

    [LoggerMessage(EventId = 213, Level = LogLevel.Debug, Message = "Connection {ConnectionId} repeated its handshake; returning the accepted response.")]
    private static partial void LogRepeatHandshake(ILogger logger, int connectionId);

    [LoggerMessage(EventId = 214, Level = LogLevel.Warning, Message = "Connection {ConnectionId} did not complete a handshake within {Timeout}; closing it.")]
    private static partial void LogHandshakeTimedOut(ILogger logger, int connectionId, TimeSpan timeout);

    [LoggerMessage(EventId = 215, Level = LogLevel.Warning, Message = "Connection {ConnectionId} still had calls in flight after the drain timeout; cancelling them.")]
    private static partial void LogDrainTimedOut(ILogger logger, int connectionId);

    [LoggerMessage(EventId = 216, Level = LogLevel.Information, Message = "Connection {ConnectionId} closed.")]
    private static partial void LogClosed(ILogger logger, int connectionId);
}
