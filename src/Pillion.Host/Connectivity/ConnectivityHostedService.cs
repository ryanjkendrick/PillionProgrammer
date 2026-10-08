using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pillion.Common.Connectivity;

namespace Pillion.Host.Connectivity;

/// <summary>
/// Runs the <see cref="ConnectivityMonitor"/> and tells the connected extension about the connectivity state with the
/// notification <see cref="NotificationName"/>: once after every accepted handshake and again on every transition.
/// </summary>
internal sealed partial class ConnectivityHostedService : BackgroundService
{
    /// <summary>The notification sent to the client. One positional argument: a <see cref="Pillion.Contracts.ConnectivityState"/> (an integer on the wire).</summary>
    public const string NotificationName = "connectivityChanged";

    private readonly ConnectivityMonitor _monitor;
    private readonly INotificationSender _sender;
    private readonly ILogger<ConnectivityHostedService> _logger;
    private readonly Channel<ConnectivityState> _outbox = Channel.CreateUnbounded<ConnectivityState>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Initializes a new instance of the <see cref="ConnectivityHostedService"/> class.</summary>
    /// <param name="monitor">The monitor to run.</param>
    /// <param name="sender">Sends notifications to the client.</param>
    /// <param name="logger">The logger.</param>
    public ConnectivityHostedService(
        ConnectivityMonitor monitor,
        INotificationSender sender,
        ILogger<ConnectivityHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(logger);

        _monitor = monitor;
        _sender = sender;
        _logger = logger;
    }

    /// <summary>Maps a monitor state to the contract enum the wire carries. The two enums have the same values.</summary>
    /// <param name="state">The monitor's state.</param>
    /// <returns>The contract state.</returns>
    public static Pillion.Contracts.ConnectivityState ToContract(ConnectivityState state) => state switch
    {
        ConnectivityState.Online => Pillion.Contracts.ConnectivityState.Online,
        ConnectivityState.Degraded => Pillion.Contracts.ConnectivityState.Degraded,
        ConnectivityState.Offline => Pillion.Contracts.ConnectivityState.Offline,
        ConnectivityState.LocalOnly => Pillion.Contracts.ConnectivityState.LocalOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown connectivity state."),
    };

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reading the settings must not delay the host becoming ready.
        await Task.Yield();

        try
        {
            _monitor.Start();
            _monitor.StateChanged += OnStateChanged;
            _sender.ClientConnected += OnClientConnected;

            // A client may already be connected when the service starts; tell it where things stand.
            _outbox.Writer.TryWrite(_monitor.State);

            var pump = PumpAsync(stoppingToken);
            await _monitor.RunAsync(stoppingToken).ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
#pragma warning disable CA1031 // Connectivity detection is optional; a failure here must not stop the host.
        catch (Exception ex)
        {
            LogFailed(_logger, ex);
        }
#pragma warning restore CA1031
        finally
        {
            _monitor.StateChanged -= OnStateChanged;
            _sender.ClientConnected -= OnClientConnected;
            _outbox.Writer.TryComplete();
        }
    }

    private void OnStateChanged(object? sender, ConnectivityChangedEventArgs e) => _outbox.Writer.TryWrite(e.NewState);

    // Read the state at the moment of the handshake, not when it is sent, so the client is never told something stale.
    private void OnClientConnected(object? sender, EventArgs e) => _outbox.Writer.TryWrite(_monitor.State);

    private async Task PumpAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var state in _outbox.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                var contractState = ToContract(state);
                if (await _sender.TrySendAsync(NotificationName, contractState).ConfigureAwait(false))
                {
                    LogSent(_logger, contractState);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    [LoggerMessage(EventId = 301, Level = LogLevel.Error, Message = "Connectivity detection stopped unexpectedly; the host keeps running without it.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 300, Level = LogLevel.Debug, Message = "Sent connectivityChanged({State}) to the client.")]
    private static partial void LogSent(ILogger logger, Pillion.Contracts.ConnectivityState state);
}
