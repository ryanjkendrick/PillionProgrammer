using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Hardware;
using Pillion.Common.Redaction;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
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
    private readonly ITokenCounter _tokens;
    private readonly IGpuTierDetector _gpu;
    private readonly AutocompleteCounters _counters;
    private readonly PromptEvaluationRates _rates;
    private readonly ContextAssembler _assembler;
    private readonly LocalResidencyMap _residency;
    private int _gpuLogged;

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
    /// <param name="tokenCounter">Counts prompt and completion tokens; <see cref="TokenCounter.Default"/> when <see langword="null"/>.</param>
    /// <param name="gpuTierDetector">Detects the GPU tier for the autocomplete budget; <see cref="GpuTierDetector.Shared"/> when <see langword="null"/>.</param>
    /// <param name="counters">The autocomplete counters; a new instance when <see langword="null"/>.</param>
    /// <param name="rates">The measured prompt-evaluation rates; a new instance when <see langword="null"/>.</param>
    /// <param name="assembler">Assembles the prompt of chat requests that carry pins, a current file or project rules; a new instance over <paramref name="tokenCounter"/> when <see langword="null"/>.</param>
    /// <param name="residency">The map of resident local models that local providers register with; a new, empty one when <see langword="null"/>.</param>
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
        ILogger<ModelGateway>? logger = null,
        ITokenCounter? tokenCounter = null,
        IGpuTierDetector? gpuTierDetector = null,
        AutocompleteCounters? counters = null,
        PromptEvaluationRates? rates = null,
        ContextAssembler? assembler = null,
        LocalResidencyMap? residency = null)
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
        _tokens = tokenCounter ?? TokenCounter.Default;
        _gpu = gpuTierDetector ?? GpuTierDetector.Shared;
        _counters = counters ?? new AutocompleteCounters();
        _counters.Attach(cache);
        _rates = rates ?? new PromptEvaluationRates();
        _assembler = assembler ?? new ContextAssembler(_tokens);
        _residency = residency ?? new LocalResidencyMap();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, TaskClass taskClass, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw new ArgumentException("A chat request needs at least one message.", nameof(request));
        }

        // A request that carries pins, a current file or project rules is assembled per candidate model (its window decides what
        // fits); any other request is sent as it is. Routing then only checks that the user's message fits: the router would
        // otherwise reject a small-window model because of history that assembly is about to compress.
        var assemble = taskClass != TaskClass.Summarise && ContextAssembler.ShouldAssemble(request);
        var route = assemble
            ? _router.Resolve(new RoutingRequest
            {
                TaskClass = taskClass,
                Profile = request.Profile,
                ModelId = request.ModelId,
                EstimatedPromptTokens = TokenEstimator.Estimate(request.Messages[^1].Content),
            })
            : _router.Resolve(taskClass, request);
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
        var chosenPromptTokens = promptTokens;
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

                var attemptMessages = messages;
                var attemptTokens = promptTokens;
                var attemptPolicy = policy;
                if (assemble)
                {
                    try
                    {
                        var assembled = await _assembler.AssembleAsync(request, BuildTarget(candidate, provider), new GatewayHistorySummariser(this), cancellationToken).ConfigureAwait(false);
                        attemptMessages = ToAiMessages(assembled.Messages);
                        attemptTokens = assembled.PromptTokens;
                        attemptPolicy = policy with
                        {
                            PromptTokens = attemptTokens,
                            PromptTokensCounted = _tokens.ForModel(candidate.ModelId).IsExact,
                            ContextItems = assembled.Report.ItemCount,
                            ContextTrimmed = assembled.Report.TrimmedCount,
                        };
                    }
                    catch (PillionRpcException ex) when (ex.Error.Code == PillionErrorCode.ContextOverflow)
                    {
                        // This model's window cannot hold the message; a fallback with a larger window may.
                        lastFailure = ex;
                        continue;
                    }
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
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [GatewayOptionKeys.Profile] = candidate.Profile,
                        [GatewayOptionKeys.RequestId] = request.RequestId,
                    },
                };
                var currentSession = session;
                attempt = new StreamAttempt<ChatResponseUpdate>(
                    _services,
                    candidate,
                    attemptPolicy with
                    {
                        FirstTokenTimeout = candidate.IsLocal ? null : _services.Options.CloudFirstTokenTimeout,
                        LocalBudget = candidate.IsLocal ? _services.Options.LocalFirstTokenBudget(attemptTokens) : null,
                    },
                    token => client.GetStreamingResponseAsync(attemptMessages, options, token),
                    update => !string.IsNullOrEmpty(update.Text),
                    () => currentSession?.Redactions.Count ?? 0,
                    cancellationToken);

                try
                {
                    await attempt.StartAsync().ConfigureAwait(false);
                    model = candidate;
                    chosenPromptTokens = attemptTokens;
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
                PromptTokens = promptReported > 0 ? promptReported : chosenPromptTokens,
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

    /// <summary>Gets the autocomplete counters (requests, cache hits and misses, model requests, drops, failures).</summary>
    public AutocompleteCounters Counters => _counters;

    /// <summary>
    /// Gets the residency map: which local models are loaded, with their context size, GPU memory and keep-alive. The status
    /// bar reads <see cref="LocalResidencyMap.GetSnapshot"/>; <see cref="LocalResidencyMap.RefreshAsync"/> asks the runtimes
    /// (at most every 30 s unless forced).
    /// </summary>
    public LocalResidencyMap Residency => _residency;

    /// <summary>Gets the measured prompt-evaluation rates of the local completion models.</summary>
    public PromptEvaluationRates PromptEvaluationRates => _rates;

    /// <summary>Gets the GPU tier the autocomplete budget is chosen by: the <c>models.gpuTier</c> setting, else the detected tier.</summary>
    /// <returns>The GPU information.</returns>
    public GpuInfo GetGpuInfo()
    {
        var setting = _registry.GetSnapshot().Settings.Settings.Models.GpuTier;
        var info = GpuTiers.Resolve(setting, _gpu);
        if (Interlocked.Exchange(ref _gpuLogged, 1) == 0)
        {
            LogGpuTier(_logger, info);
        }

        return info;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = _services.Time.GetTimestamp();
        var taskClass = request.TaskClass;
        var isAutocomplete = taskClass == TaskClass.Autocomplete;
        var multiLine = request.AllowMultiLine;
        var prefix = request.Prefix ?? string.Empty;
        var suffix = request.Suffix ?? string.Empty;
        _counters.Request();

        // Routing only checks that the budgeted prompt fits the model's context; the prompt is built per candidate.
        var route = _router.Resolve(new RoutingRequest { TaskClass = taskClass, EstimatedPromptTokens = AutocompleteBudget.GpuPromptTokens });
        LogRouted(_logger, request.RequestId, route.QualifiedId, route.Profile, route.Reason);

        // Cache: repeated keystrokes, backspaces and typing into a shown suggestion are served without a request.
        var hit = _cache.Lookup(route.ProviderId, route.ModelId, multiLine, prefix, suffix, out var cached);
        if (hit != CacheHitKind.None)
        {
            var counter = _tokens.ForModel(route.ModelId);
            var completionTokens = counter.Count(cached);
            var elapsed = _services.Time.GetElapsedTime(started);
            _services.OutboundLog.Record(new OutboundRecord(
                _services.Time.GetUtcNow() - elapsed,
                request.RequestId,
                route.ProviderId,
                route.ModelId,
                taskClass,
                route.IsLocal,
                0,
                completionTokens,
                0,
                elapsed,
                OutboundOutcome.Succeeded,
                ErrorCode: null,
                PromptTokensCounted: counter.IsExact,
                CacheHit: true,
                TimeToFirstToken: elapsed));
            yield return new CompletionChunk
            {
                RequestId = request.RequestId,
                Text = cached,
                IsFinal = true,
                ModelId = route.ModelId,
                ProviderId = route.ProviderId,
                CompletionTokens = completionTokens,
            };
            yield break;
        }

        var snapshot = _registry.GetSnapshot();
        var redactForLocal = snapshot.Settings.Settings.Privacy.RedactForLocalProviders;
        var gpu = GetGpuInfo();

        RoutedModel? model = null;
        FimPlan? plan = null;
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
                var provider = snapshot.Find(candidate.ProviderId);
                var client = provider?.GetFimClient(candidate.ModelId);
                if (provider is null || client is null)
                {
                    continue;
                }

                if (lastFailure is not null)
                {
                    _services.Progress.Report(new ModelProgress(request.RequestId, candidate.ProviderId, candidate.ModelId, ModelProgressKind.FallingBack, TimeSpan.Zero, $"Trying {candidate.QualifiedId} instead: {lastFailure.Message}"));
                }

                var candidatePlan = PlanFim(request, candidate, snapshot.FindSettings(candidate.ProviderId), gpu.Tier);
                session = provider.IsLocal && !redactForLocal ? null : new RedactionSession();
                if (session is not null)
                {
                    client = new RedactingFimClient(client, session);
                }

                var currentSession = session;
                var fimClient = client;
                var fim = candidatePlan.Request;
                _counters.ModelRequest();
                attempt = new StreamAttempt<string>(
                    _services,
                    candidate,
                    new AttemptPolicy
                    {
                        RequestId = request.RequestId,
                        TaskClass = taskClass,
                        PromptTokens = candidatePlan.Prompt.PromptTokens,
                        PromptTokensCounted = candidatePlan.Prompt.IsExact,
                        MaxRetries = isAutocomplete ? 0 : _services.Options.MaxRetries,
                        OverallDeadline = !candidate.IsLocal && isAutocomplete ? _services.Options.CloudAutocompleteTimeout : null,
                        FirstTokenTimeout = !candidate.IsLocal && !isAutocomplete ? _services.Options.CloudFirstTokenTimeout : null,
                        LocalBudget = !candidate.IsLocal ? null : isAutocomplete
                            ? _rates.BudgetFor(candidate.QualifiedId, candidatePlan.Prompt.PromptTokens, _services.Options.LocalAutocompleteFirstTokenFloor)
                            : _services.Options.LocalFirstTokenBudget(candidatePlan.Prompt.PromptTokens),
                    },
                    token => fimClient.CompleteAsync(fim, token),
                    text => text.Length > 0,
                    () => currentSession?.Redactions.Count ?? 0,
                    cancellationToken);

                try
                {
                    await attempt.StartAsync().ConfigureAwait(false);
                    model = candidate;
                    plan = candidatePlan;
                    break;
                }
                catch (AutocompleteDroppedException)
                {
                    attempt.Complete(OutboundOutcome.Dropped);
                    _counters.Dropped();
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
                    var cancelled = ex is OperationCanceledException;
                    attempt.Complete(cancelled ? OutboundOutcome.Cancelled : OutboundOutcome.Failed, ex);
                    if (cancelled)
                    {
                        _counters.Cancelled();
                    }
                    else
                    {
                        _counters.Failed();
                    }

                    throw ToRpcException(ex);
                }
            }

            if (dropped)
            {
                yield break;
            }

            if (model is null || attempt is null || plan is null)
            {
                _counters.Failed();
                throw ToRpcException(lastFailure ?? new ProviderException(
                    PillionErrorCode.ModelNotInstalled,
                    "No fill-in-the-middle model is available. Install one, for example qwen2.5-coder, or switch cloud autocomplete on.",
                    isProviderFault: false));
            }

            if (model.IsLocal && attempt.TimeToFirstToken is { } firstToken)
            {
                _rates.Observe(model.QualifiedId, plan.Prompt.PromptTokens, firstToken);
            }

            // A cloud autocomplete is buffered so the hard limit can still drop it; local completions stream. The shaper
            // applies the stop sequences and the line limit whatever the provider honoured.
            var shaper = new CompletionShaper(plan.Request.StopSequences, multiLine ? AutocompleteBudget.MultiLineMaxLines : isAutocomplete ? 1 : int.MaxValue);
            var buffer = model.IsLocal ? null : new StringBuilder();
            PillionError? streamError = null;
            var outcome = OutboundOutcome.Succeeded;
            Exception? failure = null;
            while (!shaper.IsComplete)
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
                    _counters.Cancelled();
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
                var shown = shaper.Push(text);
                if (shown.Length == 0)
                {
                    continue;
                }

                if (buffer is not null)
                {
                    buffer.Append(shown);
                    continue;
                }

                yield return new CompletionChunk { RequestId = request.RequestId, Text = shown, ModelId = model.ModelId, ProviderId = model.ProviderId };
            }

            if (dropped)
            {
                attempt.Complete(OutboundOutcome.Dropped);
                _counters.Dropped();
                yield break;
            }

            var rest = streamError is null ? shaper.Flush() : string.Empty;
            buffer?.Append(rest);
            var completion = shaper.Text;
            var counter = _tokens.ForModel(model.ModelId);
            var completionTokens = counter.Count(completion);
            attempt.ReportedCompletionTokens = completionTokens;

            if (buffer is not null && streamError is not null && isAutocomplete)
            {
                // A failed cloud suggestion is dropped too: half a suggestion is worse than none.
                attempt.Complete(outcome, failure);
                _counters.Failed();
                yield break;
            }

            attempt.Complete(outcome, failure);
            if (outcome == OutboundOutcome.Succeeded && completion.Length > 0)
            {
                _cache.Set(AutocompleteCache.CreateKey(model.ProviderId, model.ModelId, prefix, suffix, multiLine), completion);
            }
            else if (outcome == OutboundOutcome.Failed)
            {
                _counters.Failed();
            }

            yield return new CompletionChunk
            {
                RequestId = request.RequestId,
                Text = buffer?.ToString() ?? rest,
                IsFinal = true,
                ModelId = model.ModelId,
                ProviderId = model.ProviderId,
                PromptTokens = plan.Prompt.PromptTokens,
                CompletionTokens = completionTokens,
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

    /// <summary>Builds the prompt and the FIM request for one candidate model.</summary>
    /// <param name="request">The completion request.</param>
    /// <param name="candidate">The candidate model.</param>
    /// <param name="providerSettings">The candidate provider's settings entry.</param>
    /// <param name="tier">The GPU tier.</param>
    /// <returns>The plan.</returns>
    internal FimPlan PlanFim(CompletionRequest request, RoutedModel candidate, ProviderSettings? providerSettings, GpuTier tier)
    {
        var multiLine = request.AllowMultiLine;
        var maxTokens = request.MaxTokens > 0
            ? request.MaxTokens
            : request.TaskClass != TaskClass.Autocomplete ? _templates.DefaultFimMaxTokens : multiLine ? _templates.MultiLineMaxTokens : _templates.SingleLineMaxTokens;

        // The budget, never more than the model's context leaves after the completion.
        var contextWindow = _inventory.Find(candidate.ProviderId, candidate.ModelId)?.Capabilities.ContextWindow
            ?? _catalog.GetCapabilities(candidate.ModelId, candidate.IsLocal).ContextWindow;
        var budget = Math.Min(AutocompleteBudget.PromptTokensFor(candidate.IsLocal, tier), Math.Max(256, contextWindow - maxTokens));

        var format = _templates.FindFimFormat(candidate.ModelId);
        var explicitFormat = UsesExplicitFimTokens(providerSettings) ? format : null;
        var prompt = AutocompletePromptBuilder.Build(request, budget, _tokens.ForModel(candidate.ModelId), explicitFormat);

        // Stops: a single-line request ends at the first newline; then the request's own, the family's and the defaults.
        var stops = new List<string>();
        if (!multiLine && request.TaskClass == TaskClass.Autocomplete)
        {
            stops.Add("\n");
        }

        stops.AddRange((request.StopSequences ?? []).Where(s => !string.IsNullOrEmpty(s)));
        if (format is not null)
        {
            stops.AddRange(format.StopSequences);
        }

        stops.AddRange(_templates.DefaultFimStopSequences);

        var fim = new FimRequest
        {
            Prefix = explicitFormat is null ? prompt.PromptPrefix : explicitFormat.Render(prompt.PromptPrefix, prompt.Suffix),
            Suffix = explicitFormat is null ? prompt.Suffix : string.Empty,
            MaxTokens = maxTokens,
            StopSequences = stops.Distinct(StringComparer.Ordinal).ToList(),
            Temperature = _templates.DefaultFimTemperature,
            Profile = candidate.Profile,
            RequestId = request.RequestId,
        };
        return new FimPlan(fim, prompt, explicitFormat);
    }

    /// <summary>
    /// Determines whether the gateway frames FIM prompts itself for a provider: an OpenAI-compatible server whose entry sets
    /// <c>"fimTemplate": "explicit"</c> (a raw <c>/v1/completions</c> server that does not apply the model's template).
    /// Ollama always applies the model's own template.
    /// </summary>
    /// <param name="settings">The provider's settings entry.</param>
    /// <returns><see langword="true"/> when the prompt carries explicit FIM tokens.</returns>
    internal static bool UsesExplicitFimTokens(ProviderSettings? settings) =>
        settings is not null &&
        settings.Kind != ProviderKind.Ollama &&
        settings.ExtensionData?.TryGetValue("fimTemplate", out var value) == true &&
        value.ValueKind == System.Text.Json.JsonValueKind.String &&
        string.Equals(value.GetString(), "explicit", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Describes a candidate model to context assembly: its usable window (for a local Ollama runtime the fixed
    /// <c>num_ctx</c> of the routed profile when smaller than the model's own window), locality, the chat system prompt and
    /// the effective <c>indexing.exclude</c> globs.
    /// </summary>
    private AssemblyTarget BuildTarget(RoutedModel candidate, IModelProvider provider)
    {
        var window = _inventory.Find(candidate.ProviderId, candidate.ModelId)?.Capabilities.ContextWindow
            ?? _catalog.GetCapabilities(candidate.ModelId, candidate.IsLocal).ContextWindow;
        if (provider is OllamaProvider ollama)
        {
            window = Math.Min(window, ollama.Options.ContextSizeFor(candidate.Profile));
        }

        return new AssemblyTarget(candidate.ModelId, window, candidate.IsLocal, _templates.ChatSystemPrompt, _registry.GetSnapshot().Settings.Exclude);
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

    [LoggerMessage(EventId = 433, Level = LogLevel.Information, Message = "Autocomplete budget by GPU tier {Gpu}")]
    private static partial void LogGpuTier(ILogger logger, GpuInfo gpu);

    [LoggerMessage(EventId = 432, Level = LogLevel.Warning, Message = "Could not list the models of provider '{ProviderId}': {Reason}")]
    private static partial void LogListFailed(ILogger logger, string providerId, string reason);
}

/// <summary>The prompt and FIM request for one candidate model.</summary>
/// <param name="Request">The request handed to the provider's FIM client.</param>
/// <param name="Prompt">The budgeted prompt.</param>
/// <param name="ExplicitFormat">The FIM template the gateway rendered, or <see langword="null"/> when the server applies the model's template.</param>
internal sealed record FimPlan(FimRequest Request, AutocompletePrompt Prompt, FimFormat? ExplicitFormat);
