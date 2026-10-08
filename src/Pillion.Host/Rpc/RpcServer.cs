using System.IO.Pipes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pillion.Contracts;
using Pillion.Host.Lifecycle;

namespace Pillion.Host.Rpc;

/// <summary>
/// Serves RPC on the named pipe: reads the launch token, listens, serves one client at a time and listens again after
/// it disconnects. On stop it stops listening, gives in-flight calls <see cref="RpcServerOptions.DrainTimeout"/> to
/// finish and then closes the connection, which cancels the rest.
/// </summary>
internal sealed partial class RpcServer : BackgroundService
{
    private const int MaxListenRetries = 3;

    private readonly PillionHostOptions _options;
    private readonly RpcServerOptions _serverOptions;
    private readonly ILaunchTokenSource _tokenSource;
    private readonly IPipeClientProcessIdProvider _clientProcessIds;
    private readonly HostStateMachine _state;
    private readonly HostShutdown _shutdown;
    private readonly VsixConnection _vsix;
    private readonly IReadOnlyList<IRpcTarget> _targets;
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RpcServer> _logger;

    /// <summary>Initializes a new instance of the <see cref="RpcServer"/> class.</summary>
    /// <param name="options">The host options.</param>
    /// <param name="serverOptions">The RPC server timeouts.</param>
    /// <param name="tokenSource">Supplies the launch token.</param>
    /// <param name="clientProcessIds">Resolves pipe client process ids.</param>
    /// <param name="state">The host state machine.</param>
    /// <param name="shutdown">Requests a host stop.</param>
    /// <param name="vsix">The host-to-VSIX connection holder.</param>
    /// <param name="targets">The registered RPC targets.</param>
    /// <param name="services">The service provider, used to resolve targets.</param>
    /// <param name="loggerFactory">Creates loggers.</param>
    public RpcServer(
        PillionHostOptions options,
        IOptions<RpcServerOptions> serverOptions,
        ILaunchTokenSource tokenSource,
        IPipeClientProcessIdProvider clientProcessIds,
        HostStateMachine state,
        HostShutdown shutdown,
        VsixConnection vsix,
        IEnumerable<IRpcTarget> targets,
        IServiceProvider services,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(serverOptions);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options;
        _serverOptions = serverOptions.Value;
        _tokenSource = tokenSource;
        _clientProcessIds = clientProcessIds;
        _state = state;
        _shutdown = shutdown;
        _vsix = vsix;
        _targets = [.. targets];
        _services = services;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RpcServer>();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarting(_logger, HostInfo.Version, ContractVersion.Text, Environment.ProcessId, _options.PipeName, _options.ParentProcessId);

        LaunchToken token;
        try
        {
            token = await _tokenSource.GetTokenAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("could not read the launch token", ex);
            return;
        }

        var context = new RpcConnectionContext(
            token, _options, _serverOptions, _clientProcessIds, _state, _shutdown, _vsix, _targets, _services, _loggerFactory);

        var first = true;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(first);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                if (first || ++failures > MaxListenRetries)
                {
                    Fail("could not create the named pipe", ex);
                    return;
                }

                LogListenRetry(_logger, ex, failures);
                await DelayQuietly(TimeSpan.FromMilliseconds(250 * failures), stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (first)
            {
                first = false;
                LogListening(_logger, _options.PipeName);
                _state.TryTransition(HostState.Ready, "listening on the named pipe");
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException ex)
            {
                LogAcceptFailed(_logger, ex);
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            failures = 0;
            await ServeAsync(pipe, context, stoppingToken).ConfigureAwait(false);
        }

        LogStopped(_logger);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, RpcConnectionContext context, CancellationToken stoppingToken)
    {
        var connection = new RpcConnection(pipe, context);
        await using (connection.ConfigureAwait(false))
        {
            try
            {
                connection.Start();
                await connection.Completion.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await connection.DrainAsync(_serverOptions.DrainTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogConnectionFaulted(_logger, ex, connection.Id);
            }
        }
    }

    private NamedPipeServerStream CreatePipe(bool first)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (first)
        {
            // Fail rather than share the name if another process created the pipe before the host started.
            options |= PipeOptions.FirstPipeInstance;
        }

        return new NamedPipeServerStream(_options.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, options);
    }

    private void Fail(string reason, Exception exception)
    {
        LogFailed(_logger, exception, reason);
        _state.TryTransition(HostState.Failed, reason);
        _shutdown.RequestStop(HostShutdown.Failure, reason);
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping; the loop condition ends the loop.
        }
    }

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Pillion Host {Version} starting (contract {Contract}, pid {ProcessId}, pipe '{PipeName}', parent {ParentProcessId}).")]
    private static partial void LogStarting(ILogger logger, string version, string contract, int processId, string pipeName, int parentProcessId);

    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "Listening on pipe '{PipeName}'.")]
    private static partial void LogListening(ILogger logger, string pipeName);

    [LoggerMessage(EventId = 202, Level = LogLevel.Critical, Message = "Pillion Host failed: {Reason}.")]
    private static partial void LogFailed(ILogger logger, Exception exception, string reason);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "Could not listen on the pipe again (attempt {Attempt}); retrying.")]
    private static partial void LogListenRetry(ILogger logger, Exception exception, int attempt);

    [LoggerMessage(EventId = 204, Level = LogLevel.Warning, Message = "Waiting for a pipe connection failed; listening again.")]
    private static partial void LogAcceptFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 205, Level = LogLevel.Warning, Message = "Connection {ConnectionId} ended with an error.")]
    private static partial void LogConnectionFaulted(ILogger logger, Exception exception, int connectionId);

    [LoggerMessage(EventId = 206, Level = LogLevel.Information, Message = "RPC server stopped.")]
    private static partial void LogStopped(ILogger logger);
}
