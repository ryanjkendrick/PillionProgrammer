using Microsoft.Extensions.Logging;
using Pillion.Contracts;
using Pillion.Host.Connectivity;

namespace Pillion.Host.Context;

/// <summary>
/// Sends the contract 1.3 notification <see cref="NotificationName"/> (one positional argument, an <see cref="IndexStatus"/>)
/// to the connected extension, at most once per <see cref="MinimumInterval"/>: the first status after a quiet spell goes at
/// once, later ones within the window are coalesced and the latest is sent when the window ends. With no client connected
/// nothing is sent; a client that connects later asks <see cref="IContextService.GetIndexStatusAsync"/>.
/// </summary>
/// <remarks>Nothing publishes yet: the indexing pipeline (work item 2.9) calls <see cref="Publish"/> as its status changes.</remarks>
internal sealed partial class IndexStatusNotifier : IDisposable
{
    /// <summary>The notification sent to the client.</summary>
    public const string NotificationName = "indexStatusChanged";

    /// <summary>The shortest time between two notifications (at most twice a second, as the contract promises).</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(500);

    private readonly INotificationSender _sender;
    private readonly TimeProvider _time;
    private readonly ILogger<IndexStatusNotifier> _logger;
    private readonly Lock _gate = new();

    private IndexStatus? _pending;
    private DateTimeOffset? _lastSent;
    private ITimer? _timer;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="IndexStatusNotifier"/> class.</summary>
    /// <param name="sender">Sends notifications to the client.</param>
    /// <param name="time">The clock used for the throttle.</param>
    /// <param name="logger">The logger.</param>
    public IndexStatusNotifier(INotificationSender sender, TimeProvider time, ILogger<IndexStatusNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _sender = sender;
        _time = time;
        _logger = logger;
    }

    /// <summary>Publishes a new status: sends it now, or as the latest of the window when one was sent less than <see cref="MinimumInterval"/> ago.</summary>
    /// <param name="status">The status. The caller must not change it afterwards.</param>
    public void Publish(IndexStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        IndexStatus? sendNow = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = status;
            if (_timer is not null)
            {
                return; // The end of the window sends the latest.
            }

            var now = _time.GetUtcNow();
            var since = _lastSent is { } last ? now - last : TimeSpan.MaxValue;
            if (since >= MinimumInterval)
            {
                sendNow = TakePending(now);
            }
            else
            {
                _timer = _time.CreateTimer(_ => OnWindowEnded(), null, MinimumInterval - since, Timeout.InfiniteTimeSpan);
            }
        }

        if (sendNow is not null)
        {
            _ = SendAsync(sendNow);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private IndexStatus? TakePending(DateTimeOffset now)
    {
        var status = _pending;
        _pending = null;
        _lastSent = now;
        return status;
    }

    private void OnWindowEnded()
    {
        IndexStatus? status;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed)
            {
                return;
            }

            status = TakePending(_time.GetUtcNow());
        }

        if (status is not null)
        {
            _ = SendAsync(status);
        }
    }

    private async Task SendAsync(IndexStatus status)
    {
        try
        {
            if (await _sender.TrySendAsync(NotificationName, status).ConfigureAwait(false))
            {
                LogSent(_logger, status.Phase);
            }
        }
#pragma warning disable CA1031 // A failed notification must never take the indexer down; the client can poll.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogSendFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 460, Level = LogLevel.Debug, Message = "Sent indexStatusChanged ({Phase}) to the client.")]
    private static partial void LogSent(ILogger logger, IndexPhase phase);

    [LoggerMessage(EventId = 461, Level = LogLevel.Warning, Message = "Sending indexStatusChanged failed.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception);
}
