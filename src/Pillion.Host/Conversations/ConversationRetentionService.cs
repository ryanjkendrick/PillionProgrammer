using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.LocalStore.Conversations;

namespace Pillion.Host.Conversations;

/// <summary>
/// Applies the Local Store's retention rules once, shortly after the host starts (design section 10: conversations and the
/// Outbound log are kept until the user deletes them, so with the default policy this opens nothing and removes nothing).
/// It runs in the background and never delays start-up; a failure is logged.
/// </summary>
internal sealed partial class ConversationRetentionService : IHostedService, IDisposable
{
    /// <summary>How long after start-up the rules are applied, so the store is not opened while the host is still starting.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);

    private readonly ConversationRetention _retention;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _run;

    /// <summary>Initializes a new instance of the <see cref="ConversationRetentionService"/> class.</summary>
    /// <param name="retention">The retention rules.</param>
    /// <param name="time">The clock used for the start delay.</param>
    /// <param name="logger">The logger.</param>
    public ConversationRetentionService(ConversationRetention retention, TimeProvider? time = null, ILogger<ConversationRetentionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(retention);
        _retention = retention;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ConversationRetentionService>.Instance;
    }

    /// <summary>Gets the task that applies the rules, once started (for tests).</summary>
    internal Task? Run => _run;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _run = Task.Run(() => PruneAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _stopping.Dispose();

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_retention.Policy.Conversations.MaxAge is null && _retention.Policy.OutboundLog.MaxAge is null)
            {
                return; // Keep everything until the user deletes it: nothing to open.
            }

            await Task.Delay(StartDelay, _time, cancellationToken).ConfigureAwait(false);
            var result = await _retention.PruneAsync(cancellationToken).ConfigureAwait(false);
            LogPruned(_logger, result.Conversations, result.OutboundEntries);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
#pragma warning disable CA1031 // Retention is housekeeping; a failure is logged and the host carries on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPruneFailed(_logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 498, Level = LogLevel.Information, Message = "Retention removed {Conversations} conversations and {OutboundEntries} Outbound log entries.")]
    private static partial void LogPruned(ILogger logger, int conversations, int outboundEntries);

    [LoggerMessage(EventId = 499, Level = LogLevel.Warning, Message = "Applying the retention rules failed ({Error}).")]
    private static partial void LogPruneFailed(ILogger logger, string error);
}
