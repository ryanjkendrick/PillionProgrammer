using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Pillion.Common.Settings;
using System.Text;
using Pillion.Contracts;
using Pillion.Host.Conversations;
using Pillion.ModelGateway;

namespace Pillion.Host.Models;

/// <summary>
/// Implements <see cref="IModelService"/> over the Model Gateway. <see cref="CompleteAsync"/> and <see cref="ChatAsync"/>
/// stream chunks (completion also needs the <see cref="FeatureNames.Autocomplete"/> feature); <see cref="CancelAsync"/> cancels an in-flight request by its id; <see cref="ListModelsAsync"/>
/// aggregates every provider's models and marks which are usable now.
/// </summary>
internal sealed class ModelRpcService : IModelService
{
    /// <summary>The message of the error returned while the <see cref="FeatureNames.ModelGateway"/> feature is off.</summary>
    internal const string GatewayDisabledMessage =
        "The Model Gateway is turned off by settings or policy (feature modelGateway). "
        + "Set features.modelGateway to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    /// <summary>The message of the error returned while the <see cref="FeatureNames.Autocomplete"/> feature is off.</summary>
    internal const string AutocompleteDisabledMessage =
        "Autocomplete is turned off by settings or policy (feature autocomplete). "
        + "Set features.autocomplete to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    private readonly IModelGateway _gateway;
    private readonly IFeatureFlags? _features;
    private readonly ConversationRecorder? _recorder;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="ModelRpcService"/> class.</summary>
    /// <param name="gateway">The gateway.</param>
    /// <param name="features">The feature flags; while <see cref="FeatureNames.ModelGateway"/> is off every call except cancel fails with <see cref="PillionErrorCode.PolicyDenied"/>. <see langword="null"/> means no flags.</param>
    /// <param name="recorder">Saves chat requests that name a conversation to the Local Store (work item 2.2); <see langword="null"/> saves nothing.</param>
    public ModelRpcService(IModelGateway gateway, IFeatureFlags? features = null, ConversationRecorder? recorder = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
        _features = features;
        _recorder = recorder;
    }

    /// <summary>Gets the number of requests in flight (for tests).</summary>
    internal int InFlightCount => _inFlight.Count;

    /// <inheritdoc/>
    public async IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureGatewayEnabled();
        EnsureEnabled(FeatureNames.Autocomplete, AutocompleteDisabledMessage);
        using var registration = Register(request.RequestId, cancellationToken);
        await foreach (var chunk in _gateway.CompleteAsync(request, registration.Token).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureGatewayEnabled();
        using var registration = Register(request.RequestId, cancellationToken);

        // A request with a conversation id is saved: the user's message now, the reply once when the stream ends.
        var recording = _recorder is null ? null : await _recorder.BeginAsync(request, cancellationToken).ConfigureAwait(false);
        if (recording is null)
        {
            await foreach (var chunk in _gateway.ChatAsync(request, TaskClass.Chat, registration.Token).ConfigureAwait(false))
            {
                yield return chunk;
            }

            yield break;
        }

        var text = new StringBuilder();
        var reply = new ReplyTracker();
        try
        {
            await using var stream = _gateway.ChatAsync(request, TaskClass.Chat, registration.Token).GetAsyncEnumerator(registration.Token);
            while (true)
            {
                ChatChunk chunk;
                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false))
                    {
                        reply.Ended = true;
                        break;
                    }

                    chunk = stream.Current;
                }
                catch (Exception)
                {
                    reply.Failed = !registration.Token.IsCancellationRequested;
                    throw;
                }

                reply.Add(chunk, text);
                yield return chunk;
            }
        }
        finally
        {
            // Also runs when the caller stops reading (Stop, or a lost connection): what arrived is saved, marked stopped.
            if (reply.ToReply(text.ToString(), registration.Token.IsCancellationRequested) is { } finished)
            {
                await recording.CompleteAsync(finished).ConfigureAwait(false);
            }
        }
    }

    /// <summary>What the host learns from the chunks of a chat stream that is being saved.</summary>
    private sealed class ReplyTracker
    {
        private string? _modelId;
        private string? _providerId;
        private int _promptTokens;
        private int _completionTokens;
        private Citation[] _citations = [];
        private bool _final;

        /// <summary>Gets or sets a value indicating whether the stream ended by itself.</summary>
        public bool Ended { get; set; }

        /// <summary>Gets or sets a value indicating whether the stream failed (not by cancellation).</summary>
        public bool Failed { get; set; }

        public void Add(ChatChunk chunk, StringBuilder text)
        {
            text.Append(chunk.Text);
            if (!string.IsNullOrEmpty(chunk.ModelId))
            {
                _modelId = chunk.ModelId;
                _providerId = string.IsNullOrEmpty(chunk.ProviderId) ? _providerId : chunk.ProviderId;
            }

            if (chunk.IsFinal)
            {
                _final = true;
                _promptTokens = chunk.PromptTokens;
                _completionTokens = chunk.CompletionTokens;
                _citations = chunk.Citations ?? [];
                if (chunk.Error is not null)
                {
                    Failed = true; // The text is on the developer's screen with the error under it; the turn is retried, not kept.
                }
            }
        }

        public ConversationRecorder.ChatReply? ToReply(string text, bool cancelledRequest)
        {
            if (Failed)
            {
                return null; // A failed reply is not saved; the user message stays, so a Retry does not repeat it.
            }

            var finished = _final || Ended;
            var cancelled = !finished || (cancelledRequest && !_final);
            if (cancelled && text.Length == 0)
            {
                return null; // Stopped before anything arrived.
            }

            return new ConversationRecorder.ChatReply(text, _modelId, _providerId, _promptTokens, _completionTokens, _citations, cancelled);
        }
    }

    /// <inheritdoc/>
    public async Task CancelAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(requestId) && _inFlight.TryGetValue(requestId, out var source))
        {
            try
            {
                await source.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The request finished while it was being cancelled.
            }
        }
    }

    /// <inheritdoc/>
    public async Task<ModelDescriptor[]> ListModelsAsync(CancellationToken cancellationToken)
    {
        EnsureGatewayEnabled();
        return [.. await _gateway.ListModelsAsync(cancellationToken).ConfigureAwait(false)];
    }

    private void EnsureGatewayEnabled() => EnsureEnabled(FeatureNames.ModelGateway, GatewayDisabledMessage);

    private void EnsureEnabled(string feature, string message)
    {
        if (_features is not null && !_features.IsEnabled(feature))
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.PolicyDenied,
                Message = message,
            });
        }
    }

    private Registration Register(string requestId, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var key = string.IsNullOrEmpty(requestId) ? Guid.NewGuid().ToString("N") : requestId;

        // A reused id replaces the older entry; the older request can then only be cancelled by its RPC token.
        _inFlight[key] = source;
        return new Registration(this, key, source);
    }

    private sealed class Registration(ModelRpcService owner, string key, CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token => source.Token;

        public void Dispose()
        {
            owner._inFlight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, source));
            source.Dispose();
        }
    }
}
