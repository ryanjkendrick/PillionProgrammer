using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.LocalStore.Conversations;
using Pillion.ModelGateway;

namespace Pillion.Host.Conversations;

/// <summary>
/// The contract 1.3 <see cref="IConversationService"/> target, over the Local Store's <see cref="IConversationRepository"/>
/// (the encrypted <c>conversations.db</c>). Every call needs the <see cref="FeatureNames.LocalStore"/> feature
/// (<see cref="PillionErrorCode.PolicyDenied"/> naming the flag while it is off); a store that cannot be opened or used fails
/// with <see cref="PillionErrorCode.StorageUnavailable"/> (a <see cref="LocalStore.LocalStoreException"/> is passed on as it
/// is). Messages are not appended here: <see cref="ConversationRecorder"/> appends them as chat requests run.
/// </summary>
internal sealed partial class ConversationRpcService : IConversationService
{
    /// <summary>The page size when the caller asks for 0 or less.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The most conversations one page holds.</summary>
    public const int MaxPageSize = 500;

    /// <summary>The message of the error returned while the <see cref="FeatureNames.LocalStore"/> feature is off.</summary>
    internal const string LocalStoreDisabledMessage =
        "Saved conversations are turned off by settings or policy (feature localStore). "
        + "Set features.localStore to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    /// <summary>The message of the error returned when the store fails for a reason it has no specific message for.</summary>
    internal const string StoreFailedMessage = "Pillion could not read or write its saved conversations. The Pillion log has the details.";

    private readonly IConversationStore _store;
    private readonly IFeatureFlags? _features;
    private readonly StoreOutboundSink? _outboundSink;
    private readonly IOutboundLog? _outbound;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ConversationRpcService"/> class.</summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="features">The feature flags; <see langword="null"/> means every feature is on.</param>
    /// <param name="outboundSink">The Outbound log's store sink; Delete all drops what it has not written yet. May be <see langword="null"/>.</param>
    /// <param name="outbound">The in-memory Outbound log, which Delete all clears. May be <see langword="null"/>.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ConversationRpcService(
        IConversationStore store,
        IFeatureFlags? features = null,
        StoreOutboundSink? outboundSink = null,
        IOutboundLog? outbound = null,
        TimeProvider? time = null,
        ILogger<ConversationRpcService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _features = features;
        _outboundSink = outboundSink;
        _outbound = outbound;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ConversationRpcService>.Instance;
    }

    /// <inheritdoc/>
    public Task<ConversationPage> ListConversationsAsync(ConversationListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = request.Limit <= 0 ? DefaultPageSize : Math.Min(request.Limit, MaxPageSize);
        var offset = Math.Max(0, request.Offset);
        return RunAsync(
            async repository =>
            {
                // One more than asked for says whether another page follows.
                var items = await repository.ListConversationPageAsync(offset, limit + 1, cancellationToken).ConfigureAwait(false);
                return new ConversationPage
                {
                    Items = [.. items.Take(limit).Select(i => ConversationMapping.ToSummary(i.Conversation, i.MessageCount))],
                    HasMore = items.Count > limit,
                };
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ConversationDetail?> GetConversationAsync(string conversationId, CancellationToken cancellationToken) =>
        RunAsync<ConversationDetail?>(
            async repository =>
            {
                if (string.IsNullOrEmpty(conversationId) || await repository.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false) is not { } record)
                {
                    return null;
                }

                var messages = await repository.GetMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);
                var pins = await repository.GetPinsAsync(conversationId, cancellationToken).ConfigureAwait(false);
                return new ConversationDetail
                {
                    Conversation = ConversationMapping.ToSummary(record, messages.Count),
                    Messages = [.. messages.Select(ConversationMapping.ToMessage)],
                    Pins = [.. pins.Select(ConversationMapping.ToPin)],
                };
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<ConversationSummary> CreateConversationAsync(ConversationCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(
            async repository =>
            {
                var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id.Trim();
                var now = _time.GetUtcNow();
                var model = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId.Trim();
                await repository.TryCreateConversationAsync(new ConversationRecord(id, ConversationMapping.CleanTitle(request.Title), now, now, model), cancellationToken).ConfigureAwait(false);

                // An existing conversation comes back exactly as it is.
                var record = await repository.GetConversationAsync(id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The conversation vanished after it was created.");
                var count = (await repository.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false)).Count;
                return ConversationMapping.ToSummary(record, count);
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> RenameConversationAsync(string conversationId, string title, CancellationToken cancellationToken) =>
        RunAsync(
            async repository =>
            {
                if (string.IsNullOrEmpty(conversationId))
                {
                    return false;
                }

                var clean = ConversationMapping.CleanTitle(title);
                if (clean.Length == 0)
                {
                    // An empty title leaves the title as it was.
                    return await repository.GetConversationAsync(conversationId, cancellationToken).ConfigureAwait(false) is not null;
                }

                return await repository.RenameConversationAsync(conversationId, clean, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken) =>
        RunAsync(
            async repository => !string.IsNullOrEmpty(conversationId) && await repository.DeleteConversationAsync(conversationId, cancellationToken).ConfigureAwait(false),
            cancellationToken);

    /// <inheritdoc/>
    public Task<DeleteAllConversationsResult> DeleteAllConversationsAsync(CancellationToken cancellationToken) =>
        RunAsync(
            async repository =>
            {
                // Records still queued for the store must not land after the delete.
                _outboundSink?.DiscardPending();
                var result = await repository.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
                _outboundSink?.DiscardPending();
                _outbound?.Clear();
                LogDeletedAll(_logger, result.Conversations, result.Messages, result.OutboundEntries);
                return new DeleteAllConversationsResult
                {
                    Conversations = result.Conversations,
                    Messages = result.Messages,
                    Pins = result.Pins,
                    OutboundEntries = result.OutboundEntries,
                };
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<bool> SetPinsAsync(string conversationId, ConversationPin[] pins, CancellationToken cancellationToken) =>
        RunAsync(
            async repository =>
            {
                if (string.IsNullOrEmpty(conversationId))
                {
                    return false;
                }

                try
                {
                    await repository.ReplacePinsAsync(conversationId, [.. (pins ?? []).Where(p => p is not null).Select(ConversationMapping.ToRecord)], cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (KeyNotFoundException)
                {
                    return false;
                }
            },
            cancellationToken);

    private async Task<T> RunAsync<T>(Func<IConversationRepository, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (_features is not null && !_features.IsEnabled(FeatureNames.LocalStore))
        {
            throw new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = LocalStoreDisabledMessage });
        }

        try
        {
            var repository = await _store.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
            return await operation(repository).ConfigureAwait(false);
        }
        catch (PillionRpcException)
        {
            throw; // Includes LocalStoreException: its message is already written for the user.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Any other storage failure is reported as StorageUnavailable, never as an unexplained error.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogStoreFailed(_logger, ex.GetType().Name, ex);
            throw new PillionRpcException(
                new PillionError
                {
                    Code = PillionErrorCode.StorageUnavailable,
                    Message = StoreFailedMessage,
                    Details = ex.GetType().Name,
                },
                ex);
        }
    }

    [LoggerMessage(EventId = 490, Level = LogLevel.Warning, Message = "The conversation store failed ({Error}).")]
    private static partial void LogStoreFailed(ILogger logger, string error, Exception exception);

    [LoggerMessage(EventId = 491, Level = LogLevel.Information, Message = "Deleted all saved conversations: {Conversations} conversations, {Messages} messages, {OutboundEntries} Outbound log entries.")]
    private static partial void LogDeletedAll(ILogger logger, int conversations, int messages, int outboundEntries);
}
