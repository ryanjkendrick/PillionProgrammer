using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.LocalStore.Conversations;
using Pillion.ModelGateway;

namespace Pillion.Host.Conversations;

/// <summary>
/// Writes the Outbound log's records to the Local Store as they are produced, so the Outbound log view survives a restart
/// (work item 2.2). The gateway's <see cref="IOutboundLog"/> hands each record to <see cref="Write"/> on the thread that
/// recorded the request; that call only queues the record, and one background task writes them, so a slow disk or a store
/// that is still opening never delays a request. While the <see cref="FeatureNames.LocalStore"/> feature is off, or the store
/// cannot be used, records are dropped here and stay in the in-memory log only.
/// </summary>
internal sealed partial class StoreOutboundSink : IOutboundSink, IHostedService, IAsyncDisposable
{
    /// <summary>The most records waiting for the store; the oldest are dropped beyond it.</summary>
    public const int QueueCapacity = 5000;

    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly IConversationStore _store;
    private readonly IFeatureFlags? _features;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<QueueItem> _queue = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false,
    });

    private readonly object _gate = new();
    private Task? _writer;
    private long _generation;
    private DateTimeOffset _lastFailureLogged = DateTimeOffset.MinValue;
    private bool _stopped;

    /// <summary>Initializes a new instance of the <see cref="StoreOutboundSink"/> class.</summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="features">The feature flags; <see langword="null"/> means every feature is on.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public StoreOutboundSink(
        IConversationStore store,
        IFeatureFlags? features = null,
        TimeProvider? time = null,
        ILogger<StoreOutboundSink>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _features = features;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<StoreOutboundSink>.Instance;
    }

    /// <inheritdoc/>
    public void Write(OutboundRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_features is not null && !_features.IsEnabled(FeatureNames.LocalStore))
        {
            return;
        }

        EnsureWriter();
        _queue.Writer.TryWrite(new QueueItem(record, null, Volatile.Read(ref _generation)));
    }

    /// <summary>Waits until every record queued so far has been written (or dropped), for at most <paramref name="timeout"/>.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the queue has caught up or the timeout passed.</returns>
    public async Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        EnsureWriter();
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new QueueItem(null, marker, Volatile.Read(ref _generation))))
        {
            return;
        }

        try
        {
            await marker.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The writer is behind (for example the store is still opening); the caller reads what is there.
        }
    }

    /// <summary>
    /// Drops the records still waiting for the store, including one the writer already took and is waiting to write (Delete all
    /// must not be followed by a late write). Records written to the sink after this call are kept.
    /// </summary>
    public void DiscardPending()
    {
        Interlocked.Increment(ref _generation);
        while (_queue.Reader.TryRead(out var item))
        {
            item.Marker?.TrySetResult();
        }
    }

    /// <summary>Reads the newest records from the store for the Outbound log view.</summary>
    /// <param name="count">The most to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entries, oldest first; <see langword="null"/> when the store is off or cannot be read (the caller falls back to the in-memory log).</returns>
    public async Task<IReadOnlyList<OutboundLogEntry>?> GetRecentAsync(int count, CancellationToken cancellationToken)
    {
        if (_features is not null && !_features.IsEnabled(FeatureNames.LocalStore))
        {
            return null;
        }

        try
        {
            await FlushAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            var repository = await _store.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
            return await repository.GetRecentOutboundAsync(count, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The view falls back to the in-memory log; the reason is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReadFailed(_logger, ex.GetType().Name);
            return null;
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureWriter();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? writer;
        lock (_gate)
        {
            _stopped = true;
            writer = _writer;
        }

        _queue.Writer.TryComplete();
        if (writer is not null)
        {
            try
            {
                await writer.WaitAsync(StopTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The host is shutting down; records still queued stay in the in-memory log, which goes with the process.
            }
            catch (OperationCanceledException)
            {
                // Shutdown no longer waits.
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_writer is null && !_stopped)
            {
                _writer = Task.Run(WriteLoopAsync);
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        try
        {
            await foreach (var item in reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (item.Marker is { } marker)
                {
                    marker.TrySetResult();
                    continue;
                }

                await WriteOneAsync(item.Record!, item.Generation).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogWriterStopped(_logger, ex.GetType().Name);
        }
        finally
        {
            DiscardPending();
        }
    }

    private async Task WriteOneAsync(OutboundRecord record, long generation)
    {
        if ((_features is not null && !_features.IsEnabled(FeatureNames.LocalStore)) || generation != Volatile.Read(ref _generation))
        {
            return;
        }

        try
        {
            var repository = await _store.GetRepositoryAsync().ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation))
            {
                return; // Deleted while the store was opening.
            }

            await repository.AppendOutboundAsync(ToEntry(record)).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A record that cannot be stored stays in the in-memory log; one failure must not stop the writer.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var now = _time.GetUtcNow();
            if (now - _lastFailureLogged >= FailureLogInterval)
            {
                _lastFailureLogged = now;
                LogWriteFailed(_logger, ex.GetType().Name);
            }
        }
    }

    /// <summary>Maps the gateway's record to the store's entry (the same fields; enums as their names).</summary>
    /// <param name="record">The record.</param>
    /// <returns>The entry.</returns>
    internal static OutboundLogEntry ToEntry(OutboundRecord record) => new(
        record.Timestamp,
        record.RequestId,
        record.ProviderId,
        record.ModelId,
        record.TaskClass,
        record.IsLocal,
        record.EstimatedPromptTokens,
        record.CompletionTokens,
        record.RedactionCount,
        record.Duration,
        record.Outcome.ToString(),
        record.ErrorCode,
        record.PromptTokensCounted,
        record.CacheHit,
        record.TimeToFirstToken,
        record.ContextItems,
        record.ContextTrimmed);

    [LoggerMessage(EventId = 492, Level = LogLevel.Warning, Message = "Writing the Outbound log to the store failed ({Error}); records stay in the in-memory log. Logged at most once a minute.")]
    private static partial void LogWriteFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 493, Level = LogLevel.Warning, Message = "Reading the Outbound log from the store failed ({Error}); the view shows the in-memory log.")]
    private static partial void LogReadFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 494, Level = LogLevel.Error, Message = "The Outbound log writer stopped ({Error}).")]
    private static partial void LogWriterStopped(ILogger logger, string error);

    private readonly record struct QueueItem(OutboundRecord? Record, TaskCompletionSource? Marker, long Generation);
}
