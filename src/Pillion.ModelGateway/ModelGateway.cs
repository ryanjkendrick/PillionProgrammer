using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Redaction;
using Pillion.Common.Settings;
using Pillion.Contracts;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;
using ChatMessage = Pillion.Contracts.ChatMessage;
using ChatRole = Pillion.Contracts.ChatRole;

namespace Pillion.ModelGateway;

/// <summary>
/// The Model Gateway (design section 6): the only component that talks to a language model. It routes each request,
/// applies the resilience rules, redacts what crosses a cloud boundary, and records every outbound request.
/// </summary>
public interface IModelGateway
{
    /// <summary>Streams a chat response. Every chunk names the provider and model that answered; the last has <see cref="ChatChunk.IsFinal"/> set.</summary>
    /// <param name="request">The request.</param>
    /// <param name="taskClass">The task class (<see cref="TaskClass.Chat"/> for the Chat window).</param>
    /// <param name="cancellationToken">A token that cancels the stream.</param>
    /// <returns>The chunks.</returns>
    /// <exception cref="PillionRpcException">No model could serve the request.</exception>
    IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, TaskClass taskClass, CancellationToken cancellationToken);

    /// <summary>
    /// Streams a fill-in-the-middle completion. A cache hit is served without a request; a cloud autocomplete request that
    /// runs past its hard limit yields nothing.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token that cancels the stream.</param>
    /// <returns>The chunks.</returns>
    /// <exception cref="PillionRpcException">No model could serve the request.</exception>
    IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the models of every configured provider, marking which can be used now.</summary>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The models.</returns>
    Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>Probes one provider for Test connection.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="cancellationToken">A token that cancels the probe.</param>
    /// <returns>The result.</returns>
    Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken);
}

/// <summary>The services a <see cref="StreamAttempt{T}"/> uses.</summary>
internal sealed record GatewayServices(
    ModelGatewayOptions Options,
    CircuitBreakerRegistry Breakers,
    ConcurrencyLimiter Limiter,
    IOutboundLog OutboundLog,
    IProgressSink Progress,
    TimeProvider Time);

/// <summary>The default <see cref="IModelGateway"/>.</summary>
public sealed partial class ModelGateway : IModelGateway
{
    private readonly IProviderRegistry _registry;
    private readonly IModelRouter _router;
    private readonly ModelInventory _inventory;
    private readonly ModelCatalog _catalog;
    private readonly PromptTemplates _templates;
    private readonly AutocompleteCache _cache;
    private readonly IProviderReachability _reachability;
    private readonly ICostGovernor _costs;
    private readonly GatewayServices _services;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ModelGateway"/> class.</summary>
    /// <param name="registry">The configured providers.</param>
    /// <param name="router">The router.</param>
    /// <param name="inventory">The model inventory (filled by <see cref="ListModelsAsync"/>).</param>
    /// <param name="catalog">The model catalog.</param>
    /// <param name="templates">The prompt templates.</param>
    /// <param name="cache">The autocomplete cache.</param>
    /// <param name="breakers">The circuit breakers.</param>
    /// <param name="limiter">The concurrency limiter.</param>
    /// <param name="outboundLog">The Outbound log.</param>
    /// <param name="reachability">Connectivity per provider.</param>
    /// <param name="costs">The spend caps.</param>
    /// <param name="progress">Receives progress notices.</param>
    /// <param name="options">The gateway options.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public ModelGateway(
        IProviderRegistry registry,
        IModelRouter router,
        ModelInventory inventory,
        ModelCatalog catalog,
        PromptTemplates templates,
        AutocompleteCache cache,
        CircuitBreakerRegistry breakers,
        ConcurrencyLimiter limiter,
        IOutboundLog outboundLog,
        IProviderReachability reachability,
        ICostGovernor costs,
        IProgressSink progress,
        ModelGatewayOptions options,
        TimeProvider? time = null,
        ILogger<ModelGateway>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _reachability = reachability ?? throw new ArgumentNullException(nameof(reachability));
        _costs = costs ?? throw new ArgumentNullException(nameof(costs));
        _services = new GatewayServices(
            options ?? throw new ArgumentNullException(nameof(options)),
            breakers ?? throw new ArgumentNullException(nameof(breakers)),
            limiter ?? throw new ArgumentNullException(nameof(limiter)),
            outboundLog ?? throw new ArgumentNullException(nameof(outboundLog)),
            progress ?? throw new ArgumentNullException(nameof(progress)),
            time ?? TimeProvider.System);
        _logger = logger ?? NullLogger<ModelGateway>.Instance;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, TaskClass taskClass, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw new ArgumentException("A chat request needs at least one message.", nameof(request));
        }

        var route = _router.Resolve(taskClass, request);
        LogRouted(_logger, request.RequestId, route.QualifiedId, route.Profile, route.Reason);

        var messages = ToAiMessages(request.Messages);
        var promptTokens = TokenEstimator.Estimate(messages.Select(m => m.Text));
        var redactForLocal = _registry.GetSnapshot().Settings.Settings.Privacy.RedactForLocalProviders;
        var policy = new AttemptPolicy
        {
            RequestId = request.RequestId,
            TaskClass = taskClass,
            PromptTokens = promptTokens,
            MaxRetries = _services.Options.MaxRetries,
        };

        RoutedModel? model = null;
        StreamAttempt<ChatResponseUpdate>? attempt = null;
        RedactionSession? session = null;
        Exception? lastFailure = null;
        var candidates = new List<RoutedModel> { route };
        candidates.AddRange(route.Fallbacks);

        try
        {
            foreach (var candidate in candidates)
            {
                var provider = _registry.GetSnapshot().Find(candidate.ProviderId);
                if (provider is null)
                {
                    continue;
                }

                if (lastFailure is not null)
                {
                    _services.Progress.Report(new ModelProgress(request.RequestId, candidate.ProviderId, candidate.ModelId, ModelProgressKind.FallingBack, TimeSpan.Zero, $"Trying {candidate.QualifiedId} instead: {lastFailure.Message}"));
                }

                session = provider.IsLocal && !redactForLocal ? null : new RedactionSession();
                var client = provider.GetChatClient(candidate.ModelId);
                if (session is not null)
                {
                    client = new RedactingChatClient(client, session);
                }

                var options = new ChatOptions
                {
                    ModelId = candidate.ModelId,
                    AdditionalProperties = new AdditionalPropertiesDictionary { [GatewayOptionKeys.Profile] = candidate.Profile },
                };
                var currentSession = session;
                attempt = new StreamAttempt<ChatResponseUpdate>(
                    _services,
                    candidate,
                    policy with
                    {
                        FirstTokenTimeout = candidate.IsLocal ? null : _services.Options.CloudFirstTokenTimeout,
                        LocalBudget = candidate.IsLocal ? _services.Options.LocalFirstTokenBudget(promptTokens) : null,
                    },
                    token => client.GetStreamingResponseAsync(messages, options, token),
                    update => !string.IsNullOrEmpty(update.Text),
                    () => currentSession?.Redactions.Count ?? 0,
                    cancellationToken);

                try
                {
                    await attempt.StartAsync().ConfigureAwait(false);
                    model = candidate;
                    break;
                }
                catch (ProviderException ex) when (ex.Code != PillionErrorCode.ContextOverflow)
                {
                    attempt.Complete(OutboundOutcome.Failed, ex);
                    await attempt.DisposeAsync().ConfigureAwait(false);
                    attempt = null;
                    session?.Dispose();
                    session = null;
                    lastFailure = ex;
                    LogAttemptFailed(_logger, request.RequestId, candidate.QualifiedId, ex.Message);
                }
                catch (Exception ex)
                {
                    attempt.Complete(ex is OperationCanceledException ? OutboundOutcome.Cancelled : OutboundOutcome.Failed, ex);
                    throw ToRpcException(ex);
                }
            }

            if (model is null || attempt is null)
            {
                throw ToRpcException(lastFailure ?? new ProviderException("No model provider could serve the request."));
            }

            // Stream. A failure after text was sent ends the stream with an error on the final chunk.
            var promptReported = 0;
            var completionReported = 0;
            PillionError? streamError = null;
            var outcome = OutboundOutcome.Succeeded;
            Exception? failure = null;
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await attempt.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = attempt.Current;
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    attempt.Complete(OutboundOutcome.Cancelled, ex);
                    throw;
                }
                catch (Exception ex) when (ex is ProviderException or PillionRpcException)
                {
                    failure = ex;
                    outcome = OutboundOutcome.Failed;
                    streamError = ex is ProviderException p ? p.ToError() : ((PillionRpcException)ex).Error;
                    break;
                }

                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    promptReported = (int)(usage.Details.InputTokenCount ?? promptReported);
                    completionReported = (int)(usage.Details.OutputTokenCount ?? completionReported);
                }

                var text = update.Text;
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                attempt.GeneratedCharacters += text.Length;
                yield return new ChatChunk
                {
                    RequestId = request.RequestId,
                    Text = text,
                    ModelId = model.ModelId,
                    ProviderId = model.ProviderId,
                };
            }

            attempt.ReportedCompletionTokens = completionReported;
            attempt.Complete(outcome, failure);
            yield return new ChatChunk
            {
                RequestId = request.RequestId,
                IsFinal = true,
                ModelId = model.ModelId,
                ProviderId = model.ProviderId,
                PromptTokens = promptReported > 0 ? promptReported : promptTokens,
                CompletionTokens = completionReported > 0 ? completionReported : (int)((attempt.GeneratedCharacters + 3) / 4),
                Error = streamError,
            };
        }
        finally
        {
            if (attempt is not null)
            {
                await attempt.DisposeAsync().ConfigureAwait(false);
            }

            session?.Dispose();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var taskClass = request.TaskClass;
        var fim = new FimRequest
        {
            Prefix = request.Prefix ?? string.Empty,
            Suffix = request.Suffix ?? string.Empty,
            MaxTokens = request.MaxTokens > 0 ? request.MaxTokens : _templates.DefaultFimMaxTokens,
            StopSequences = _templates.DefaultFimStopSequences,
            Temperature = _templates.DefaultFimTemperature,
        };

        var route = _router.Resolve(taskClass, fim);
        LogRouted(_logger, request.RequestId, route.QualifiedId, route.Profile, route.Reason);

        // Cache: repeated keystrokes and backspaces are served without a request.
        var cacheKey = AutocompleteCache.CreateKey(route.ProviderId, route.ModelId, fim.Prefix, fim.Suffix);
        if (_cache.TryGet(cacheKey, out var cached))
        {
            yield return new CompletionChunk
            {
                RequestId = request.RequestId,
                Text = cached,
                IsFinal = true,
                ModelId = route.ModelId,
                ProviderId = route.ProviderId,
                CompletionTokens = TokenEstimator.Estimate(cached),
            };
            yield break;
        }

        var promptTokens = TokenEstimator.Estimate(fim.Prefix) + TokenEstimator.Estimate(fim.Suffix);
        var redactForLocal = _registry.GetSnapshot().Settings.Settings.Privacy.RedactForLocalProviders;
        var isAutocomplete = taskClass == TaskClass.Autocomplete;
        var policy = new AttemptPolicy
        {
            RequestId = request.RequestId,
            TaskClass = taskClass,
            PromptTokens = promptTokens,
            MaxRetries = isAutocomplete ? 0 : _services.Options.MaxRetries,
        };

        RoutedModel? model = null;
        StreamAttempt<string>? attempt = null;
        RedactionSession? session = null;
        Exception? lastFailure = null;
        var candidates = new List<RoutedModel> { route };
        candidates.AddRange(route.Fallbacks);
        var dropped = false;

        try
        {
            foreach (var candidate in candidates)
            {
                var provider = _registry.GetSnapshot().Find(candidate.ProviderId);
                var client = provider?.GetFimClient(candidate.ModelId);
                if (provider is null || client is null)
                {
                    continue;
                }

                session = provider.IsLocal && !redactForLocal ? null : new RedactionSession();
                if (session is not null)
                {
                    client = new RedactingFimClient(client, session);
                }

                var routedFim = fim with { Profile = candidate.Profile };
                var currentSession = session;
                var fimClient = client;
                attempt = new StreamAttempt<string>(
                    _services,
                    candidate,
                    policy with
                    {
                        OverallDeadline = !candidate.IsLocal && isAutocomplete ? _services.Options.CloudAutocompleteTimeout : null,
                        FirstTokenTimeout = !candidate.IsLocal && !isAutocomplete ? _services.Options.CloudFirstTokenTimeout : null,
                        LocalBudget = candidate.IsLocal ? _services.Options.LocalFirstTokenBudget(promptTokens) : null,
                    },
                    token => fimClient.CompleteAsync(routedFim, token),
                    text => text.Length > 0,
                    () => currentSession?.Redactions.Count ?? 0,
                    cancellationToken);

                try
                {
                    await attempt.StartAsync().ConfigureAwait(false);
                    model = candidate;
                    break;
                }
                catch (AutocompleteDroppedException)
                {
                    attempt.Complete(OutboundOutcome.Dropped);
                    dropped = true;
                    break;
                }
                catch (ProviderException ex) when (ex.Code != PillionErrorCode.ContextOverflow)
                {
                    attempt.Complete(OutboundOutcome.Failed, ex);
                    await attempt.DisposeAsync().ConfigureAwait(false);
                    attempt = null;
                    session?.Dispose();
                    session = null;
                    lastFailure = ex;
                    LogAttemptFailed(_logger, request.RequestId, candidate.QualifiedId, ex.Message);
                }
                catch (Exception ex)
                {
                    attempt.Complete(ex is OperationCanceledException ? OutboundOutcome.Cancelled : OutboundOutcome.Failed, ex);
                    throw ToRpcException(ex);
                }
            }

            if (dropped)
            {
                yield break;
            }

            if (model is null || attempt is null)
            {
                throw ToRpcException(lastFailure ?? new ProviderException(
                    PillionErrorCode.ModelNotInstalled,
                    "No fill-in-the-middle model is available. Install one, for example qwen2.5-coder, or switch cloud autocomplete on.",
                    isProviderFault: false));
            }

            // A cloud autocomplete is buffered so the hard limit can still drop it; local completions stream.
            var buffer = model.IsLocal ? null : new StringBuilder();
            var all = new StringBuilder();
            PillionError? streamError = null;
            var outcome = OutboundOutcome.Succeeded;
            Exception? failure = null;
            while (true)
            {
                string text;
                try
                {
                    if (!await attempt.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    text = attempt.Current;
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    attempt.Complete(OutboundOutcome.Cancelled, ex);
                    throw;
                }
                catch (AutocompleteDroppedException)
                {
                    dropped = true;
                    break;
                }
                catch (Exception ex) when (ex is ProviderException or PillionRpcException)
                {
                    failure = ex;
                    outcome = OutboundOutcome.Failed;
                    streamError = ex is ProviderException p ? p.ToError() : ((PillionRpcException)ex).Error;
                    break;
                }

                if (text.Length == 0)
                {
                    continue;
                }

                attempt.GeneratedCharacters += text.Length;
                all.Append(text);
                if (buffer is not null)
                {
                    buffer.Append(text);
                    continue;
                }

                yield return new CompletionChunk { RequestId = request.RequestId, Text = text, ModelId = model.ModelId, ProviderId = model.ProviderId };
            }

            if (dropped)
            {
                attempt.Complete(OutboundOutcome.Dropped);
                yield break;
            }

            if (buffer is not null && streamError is not null && isAutocomplete)
            {
                // A failed cloud suggestion is dropped too: half a suggestion is worse than none.
                attempt.Complete(outcome, failure);
                yield break;
            }

            attempt.Complete(outcome, failure);
            if (outcome == OutboundOutcome.Succeeded && all.Length > 0)
            {
                _cache.Set(AutocompleteCache.CreateKey(model.ProviderId, model.ModelId, fim.Prefix, fim.Suffix), all.ToString());
            }

            yield return new CompletionChunk
            {
                RequestId = request.RequestId,
                Text = buffer?.ToString() ?? string.Empty,
                IsFinal = true,
                ModelId = model.ModelId,
                ProviderId = model.ProviderId,
                PromptTokens = promptTokens,
                CompletionTokens = (int)((attempt.GeneratedCharacters + 3) / 4),
                Error = streamError,
            };
        }
        finally
        {
            if (attempt is not null)
            {
                await attempt.DisposeAsync().ConfigureAwait(false);
            }

            session?.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var snapshot = _registry.GetSnapshot();
        var localOnly = snapshot.Settings.LocalOnly;

        var tasks = snapshot.Providers.Select(async provider =>
        {
            var configured = ConfiguredModels(snapshot.FindSettings(provider.Id)!, provider.IsLocal);
            if (localOnly && !provider.IsLocal)
            {
                // Local-only mode contacts no cloud provider, not even to list models.
                return configured.Select(m => m.ToDescriptor(isAvailable: false)).ToList();
            }

            var usable = _reachability.IsReachable(provider.Id) && _services.Breakers.IsAvailable(provider.Id) && _costs.IsWithinCap(provider.Id);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_services.Options.ListModelsTimeout);
                var models = await provider.ListModelsAsync(timeout.Token).ConfigureAwait(false);
                _inventory.Update(provider.Id, models);
                return models.Select(m => m.ToDescriptor(usable)).ToList();
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                LogListFailed(_logger, provider.Id, ex.Message);
                return configured.Select(m => m.ToDescriptor(isAvailable: false)).ToList();
            }
        });

        var lists = await Task.WhenAll(tasks).ConfigureAwait(false);
        var result = lists.SelectMany(l => l).ToList();

        foreach (var hidden in snapshot.Hidden)
        {
            result.AddRange(ConfiguredModels(hidden.Settings, isLocal: false).Select(m => m.ToDescriptor(isAvailable: false)));
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var snapshot = _registry.GetSnapshot();
        var provider = snapshot.Find(providerId);
        if (provider is null)
        {
            var hidden = snapshot.Hidden.FirstOrDefault(h => string.Equals(h.Settings.Id, providerId, StringComparison.OrdinalIgnoreCase));
            return new ProviderTestResult
            {
                ProviderId = providerId,
                IsSuccess = false,
                Message = hidden?.Reason ?? $"No provider with id '{providerId}' is configured.",
            };
        }

        if (snapshot.Settings.LocalOnly && !provider.IsLocal)
        {
            return new ProviderTestResult
            {
                ProviderId = provider.Id,
                IsSuccess = false,
                Message = "Local-only mode is on, so cloud providers are not contacted.",
            };
        }

        var probe = await provider.ProbeAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderTestResult { ProviderId = provider.Id, IsSuccess = probe.IsReachable, Message = probe.Message };
    }

    private List<ProviderModel> ConfiguredModels(ProviderSettings settings, bool isLocal)
    {
        var ids = settings.Models.ToList();
        if (ids.Count == 0)
        {
            foreach (var profile in Enum.GetValues<Profile>())
            {
                if (profile != Profile.Auto && _catalog.GetDefaultModel(settings.Kind, profile) is { } model && !ids.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(model);
                }
            }
        }

        return ids.Select(id => new ProviderModel(settings.Id, id, id, _catalog.GetCapabilities(id, isLocal))).ToList();
    }

    private List<AiChatMessage> ToAiMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<AiChatMessage>(messages.Count + 1);
        if (!messages.Any(m => m.Role == ChatRole.System))
        {
            result.Add(new AiChatMessage(AiChatRole.System, _templates.ChatSystemPrompt));
        }

        foreach (var message in messages)
        {
            var role = message.Role switch
            {
                ChatRole.System => AiChatRole.System,
                ChatRole.Assistant => AiChatRole.Assistant,
                ChatRole.Tool => AiChatRole.Tool,
                _ => AiChatRole.User,
            };
            result.Add(new AiChatMessage(role, message.Content ?? string.Empty));
        }

        return result;
    }

    private static Exception ToRpcException(Exception ex) => ex switch
    {
        PillionRpcException or OperationCanceledException => ex,
        ProviderException provider => new PillionRpcException(provider.ToError(), provider),
        _ => ex,
    };

    [LoggerMessage(EventId = 430, Level = LogLevel.Information, Message = "Request {RequestId} routed to {Model} ({Profile}): {Reason}")]
    private static partial void LogRouted(ILogger logger, string requestId, string model, Profile profile, string reason);

    [LoggerMessage(EventId = 431, Level = LogLevel.Warning, Message = "Request {RequestId} failed on {Model}: {Reason}")]
    private static partial void LogAttemptFailed(ILogger logger, string requestId, string model, string reason);

    [LoggerMessage(EventId = 432, Level = LogLevel.Warning, Message = "Could not list the models of provider '{ProviderId}': {Reason}")]
    private static partial void LogListFailed(ILogger logger, string providerId, string reason);
}
