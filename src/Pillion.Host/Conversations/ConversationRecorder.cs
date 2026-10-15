using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.LocalStore.Conversations;
using Pillion.ModelGateway;

namespace Pillion.Host.Conversations;

/// <summary>
/// Appends a chat turn to the Local Store as <c>IModelService.ChatAsync</c> runs it (work item 2.2): the user's message when the
/// request starts, the reply once, when the stream ends. A request whose <see cref="ChatRequest.ConversationId"/> names no
/// stored conversation creates it, titled from the first message. Nothing here may fail a chat request: every storage
/// problem is logged and the chat goes on unsaved.
/// </summary>
internal sealed partial class ConversationRecorder
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly IConversationStore _store;
    private readonly IFeatureFlags? _features;
    private readonly ModelCatalog _catalog;
    private readonly IOutboundLog? _outbound;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ConversationRecorder"/> class.</summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="features">The feature flags; <see langword="null"/> means every feature is on.</param>
    /// <param name="catalog">The model catalog, for the cost of a cloud reply; <see langword="null"/> for the shipped one.</param>
    /// <param name="outbound">The Outbound log, to learn whether a reply's provider was local; <see langword="null"/> when cost is not recorded.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ConversationRecorder(
        IConversationStore store,
        IFeatureFlags? features = null,
        ModelCatalog? catalog = null,
        IOutboundLog? outbound = null,
        TimeProvider? time = null,
        ILogger<ConversationRecorder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _features = features;
        _catalog = catalog ?? ModelCatalog.Default;
        _outbound = outbound;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ConversationRecorder>.Instance;
    }

    /// <summary>
    /// Records the start of a chat request: creates the conversation when it is new and appends the user's message (unless it is
    /// already the newest message, as when a failed reply is retried).
    /// </summary>
    /// <param name="request">The chat request.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The recording to finish when the reply ends, or <see langword="null"/> when the request is not saved (no conversation id, the feature is off, or the store failed).</returns>
    public async Task<ChatRecording?> BeginAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.ConversationId)
            || (_features is not null && !_features.IsEnabled(FeatureNames.LocalStore)))
        {
            return null;
        }

        try
        {
            var repository = await _store.GetRepositoryAsync(cancellationToken).ConfigureAwait(false);
            var userText = request.Messages?.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
            var title = ConversationMapping.TitleFrom(userText);
            var now = _time.GetUtcNow();
            var created = await repository.TryCreateConversationAsync(new ConversationRecord(request.ConversationId, title, now, now), cancellationToken).ConfigureAwait(false);
            if (!created && title.Length > 0 && await repository.GetConversationAsync(request.ConversationId, cancellationToken).ConfigureAwait(false) is { Title.Length: 0 })
            {
                // Created empty by the Chat window (pins before the first message): the first message names it.
                await repository.RenameConversationAsync(request.ConversationId, title, cancellationToken).ConfigureAwait(false);
            }

            if (userText.Length > 0)
            {
                var last = created ? null : await repository.GetLastMessageAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
                if (last is not { Role: "user" } || !string.Equals(last.Content, userText, StringComparison.Ordinal))
                {
                    await repository.AddMessageAsync(new NewMessage(request.ConversationId, "user", userText, now), cancellationToken).ConfigureAwait(false);
                }
            }

            return new ChatRecording(this, request.ConversationId, request.RequestId);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
#pragma warning disable CA1031 // A conversation that cannot be saved must not stop the chat.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBeginFailed(_logger, ex.GetType().Name);
            return null;
        }
    }

    private async Task CompleteAsync(string conversationId, string requestId, ChatReply reply)
    {
        // The request may have been cancelled: the write still happens, bounded by its own time limit.
        using var timeout = new CancellationTokenSource(WriteTimeout);
        try
        {
            var repository = await _store.GetRepositoryAsync(timeout.Token).ConfigureAwait(false);
            var model = string.IsNullOrEmpty(reply.ModelId) ? null : reply.ModelId;
            var provider = string.IsNullOrEmpty(reply.ProviderId) ? null : reply.ProviderId;
            await repository.AddMessageAsync(
                new NewMessage(
                    conversationId,
                    "assistant",
                    reply.Text,
                    _time.GetUtcNow(),
                    model,
                    provider,
                    reply.PromptTokens > 0 ? reply.PromptTokens : null,
                    reply.CompletionTokens > 0 ? reply.CompletionTokens : null,
                    CostOf(requestId, model, reply.PromptTokens, reply.CompletionTokens),
                    ConversationMapping.WriteCitations(reply.Citations),
                    reply.Cancelled),
                timeout.Token).ConfigureAwait(false);
            if (model is not null)
            {
                await repository.SetConversationModelAsync(conversationId, provider is null ? model : provider + "/" + model, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (KeyNotFoundException)
        {
            // The conversation was deleted while the reply streamed: the reply is not saved.
            LogDeletedWhileStreaming(_logger);
        }
#pragma warning disable CA1031 // A reply that cannot be saved must not fail the request; the user already has it on screen.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCompleteFailed(_logger, ex.GetType().Name);
        }
    }

    /// <summary>The cost in US dollars of a reply: 0 for a local model, from the catalog's prices for a cloud one, <see langword="null"/> when unknown.</summary>
    private double? CostOf(string requestId, string? modelId, int promptTokens, int completionTokens)
    {
        if (_outbound is null || string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(modelId))
        {
            return null;
        }

        var record = _outbound.GetRecent().LastOrDefault(r => string.Equals(r.RequestId, requestId, StringComparison.Ordinal) && r.TaskClass == TaskClass.Chat);
        if (record is null)
        {
            return null;
        }

        if (record.IsLocal)
        {
            return 0;
        }

        var prices = _catalog.GetCapabilities(modelId, isLocal: false);
        if (prices.InputCostPerMillionTokens <= 0 && prices.OutputCostPerMillionTokens <= 0)
        {
            return null; // The catalog does not know the price.
        }

        var prompt = promptTokens > 0 ? promptTokens : record.EstimatedPromptTokens;
        var completion = completionTokens > 0 ? completionTokens : record.CompletionTokens;
        return (double)((prompt * prices.InputCostPerMillionTokens) + (completion * prices.OutputCostPerMillionTokens)) / 1_000_000d;
    }

    [LoggerMessage(EventId = 495, Level = LogLevel.Warning, Message = "Saving the start of a chat turn failed ({Error}); the turn is not saved.")]
    private static partial void LogBeginFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 496, Level = LogLevel.Warning, Message = "Saving a chat reply failed ({Error}); the reply is not saved.")]
    private static partial void LogCompleteFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 497, Level = LogLevel.Debug, Message = "The conversation was deleted while its reply streamed; the reply is not saved.")]
    private static partial void LogDeletedWhileStreaming(ILogger logger);

    /// <summary>A chat turn being recorded: finish it with <see cref="CompleteAsync"/> when the stream ends.</summary>
    public sealed class ChatRecording
    {
        private readonly ConversationRecorder _owner;
        private readonly string _conversationId;
        private readonly string _requestId;
        private int _completed;

        internal ChatRecording(ConversationRecorder owner, string conversationId, string requestId)
        {
            _owner = owner;
            _conversationId = conversationId;
            _requestId = requestId;
        }

        /// <summary>Saves the reply. Only the first call writes. Never throws.</summary>
        /// <param name="reply">The reply as it ended.</param>
        /// <returns>A task that completes when the reply is saved (or could not be).</returns>
        public Task CompleteAsync(ChatReply reply)
        {
            ArgumentNullException.ThrowIfNull(reply);
            return Interlocked.Exchange(ref _completed, 1) == 0
                ? _owner.CompleteAsync(_conversationId, _requestId, reply)
                : Task.CompletedTask;
        }
    }

    /// <summary>A reply as it ended.</summary>
    /// <param name="Text">The text received.</param>
    /// <param name="ModelId">The model that answered.</param>
    /// <param name="ProviderId">The provider that served it.</param>
    /// <param name="PromptTokens">Prompt tokens, 0 when unknown.</param>
    /// <param name="CompletionTokens">Generated tokens, 0 when unknown.</param>
    /// <param name="Citations">The citations from the final chunk.</param>
    /// <param name="Cancelled">Whether the reply was stopped before it finished.</param>
    public sealed record ChatReply(
        string Text,
        string? ModelId,
        string? ProviderId,
        int PromptTokens,
        int CompletionTokens,
        Citation[] Citations,
        bool Cancelled);
}
