using System.Text.RegularExpressions;
using Pillion.Common.Redaction;
using Pillion.Contracts;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Providers.Ollama;

namespace Pillion.Host.Models;

/// <summary>
/// The Context Inspector's preview (work item 1.13): assembles a chat request the way the Model Gateway would for the
/// model the request would be routed to, without calling any model, and reports every item with its token count.
/// </summary>
/// <remarks>
/// <para>
/// Routing and the assembly target mirror <c>ModelGateway.ChatAsync</c>: the router sees only the size of the user's
/// message, and the target's window is the inventory's (else the catalog's) window, capped for a local Ollama runtime at
/// the fixed <c>num_ctx</c> of the routed profile. Each preview uses its own <see cref="ContextAssembler"/>, so its summary
/// cache never changes what sending reuses. History that sending would compress is summarised by a stand-in that calls no
/// model; the report says so and counts the summary at its cap.
/// </para>
/// <para>
/// When the target is a cloud model (or redaction for local providers is on), every item that would be sent is run through
/// the secret scanner and the matches are counted: the same scanner the gateway's redaction uses, so the count is what
/// sending would replace, give or take the parts of a truncated pin that are cut.
/// </para>
/// </remarks>
internal sealed class ContextPreview
{
    private readonly IModelRouter _router;
    private readonly IProviderRegistry _registry;
    private readonly ModelInventory _inventory;
    private readonly ModelCatalog _catalog;
    private readonly PromptTemplates _templates;
    private readonly ITokenCounter _tokens;
    private readonly ContextAssemblerOptions _options;
    private readonly IReadOnlyList<IContextSource> _sources;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="ContextPreview"/> class.</summary>
    /// <param name="router">Routes the request.</param>
    /// <param name="registry">The configured providers and the effective settings.</param>
    /// <param name="inventory">The models the providers listed (for the context window).</param>
    /// <param name="catalog">The model catalog (the window of a model the inventory does not know).</param>
    /// <param name="templates">The prompt templates (the chat system prompt).</param>
    /// <param name="tokens">Counts tokens per model family.</param>
    /// <param name="options">The assembly limits the gateway uses.</param>
    /// <param name="sources">The phase 2 context sources; none when <see langword="null"/>.</param>
    /// <param name="time">The clock.</param>
    public ContextPreview(
        IModelRouter router,
        IProviderRegistry registry,
        ModelInventory inventory,
        ModelCatalog catalog,
        PromptTemplates templates,
        ITokenCounter tokens,
        ContextAssemblerOptions options,
        IEnumerable<IContextSource>? sources = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);
        _router = router;
        _registry = registry;
        _inventory = inventory;
        _catalog = catalog;
        _templates = templates;
        _tokens = tokens;
        _options = options;
        _sources = [.. sources ?? []];
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Previews the context of a chat request.</summary>
    /// <param name="request">The request about to be sent; its last message is the query.</param>
    /// <param name="cancellationToken">A token that cancels the preview.</param>
    /// <returns>The report.</returns>
    /// <exception cref="PillionRpcException">No model can serve the request, or the message alone does not fit its window.</exception>
    public async Task<ContextReportDto> PreviewAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw new ArgumentException("A chat request needs at least one message.", nameof(request));
        }

        var route = _router.Resolve(new RoutingRequest
        {
            TaskClass = TaskClass.Chat,
            Profile = request.Profile,
            ModelId = request.ModelId,
            EstimatedPromptTokens = TokenEstimator.Estimate(request.Messages[^1].Content),
        });
        var snapshot = _registry.GetSnapshot();
        var target = BuildTarget(route, snapshot.Find(route.ProviderId), snapshot.Settings.Exclude);
        var summariser = new StandInSummariser();
        var assembler = new ContextAssembler(_tokens, _options, _sources, logger: null, _time);
        var assembled = await assembler.AssembleAsync(request, target, summariser, cancellationToken).ConfigureAwait(false);

        var redacts = !route.IsLocal || snapshot.Settings.Settings.Privacy.RedactForLocalProviders;
        var report = assembled.Report;
        var pinIndexes = (request.Pins ?? []).Select((pin, index) => (pin, index)).Where(p => p.pin is not null).Select(p => p.index).ToList();
        var nextPin = 0;
        var items = new List<ContextReportItemDto>(report.Items.Count);
        foreach (var item in report.Items)
        {
            var pinIndex = item.Kind == ContextItemKind.Pin && nextPin < pinIndexes.Count ? pinIndexes[nextPin++] : -1;
            var dto = new ContextReportItemDto
            {
                Kind = KindName(item.Kind),
                Name = item.Name,
                Tokens = item.Tokens,
                OriginalTokens = item.OriginalTokens,
                Truncated = item.Truncated,
                Dropped = item.Dropped,
                Note = item.Note,
                PinIndex = pinIndex,
                ContextKey = item.Key,
            };

            if (item.Kind == ContextItemKind.Summary)
            {
                // The stand-in wrote a placeholder; sending asks a model for a summary of at most this many tokens.
                dto.Tokens = Math.Max(dto.Tokens, summariser.MaxTokens);
                dto.Note = $"Sending summarises these older messages into at most {summariser.MaxTokens} tokens (unless a summary is already cached); the count shown is that limit.";
            }

            if (redacts && !item.Dropped)
            {
                dto.RedactionCount = CountSecrets(TextOf(item, pinIndex, request, target.SystemPrompt, assembled));
            }

            items.Add(dto);
        }

        return new ContextReportDto
        {
            ModelId = route.ModelId,
            ProviderId = route.ProviderId,
            IsLocal = route.IsLocal,
            ContextWindow = report.ContextWindow,
            OutputReserve = report.OutputReserve,
            BudgetTokens = report.BudgetTokens,
            TotalTokens = items.Sum(i => i.Tokens),
            Items = [.. items],
            IsAssembled = await assembler.ShouldAssembleAsync(request, cancellationToken).ConfigureAwait(false),
            Redacts = redacts,
            RedactionCount = items.Sum(i => i.RedactionCount),
        };
    }

    /// <summary>The name of an item kind on the wire.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The name.</returns>
    internal static string KindName(ContextItemKind kind) => kind switch
    {
        ContextItemKind.SystemPrompt => "system",
        ContextItemKind.Rules => "rules",
        ContextItemKind.RepoMap => "repoMap",
        ContextItemKind.Pin => "pin",
        ContextItemKind.CurrentFile => "currentFile",
        ContextItemKind.RetrievedChunks => "retrieved",
        ContextItemKind.Summary => "summary",
        ContextItemKind.History => "history",
        _ => "query",
    };

    /// <summary>Counts the secrets in a text; a scan that times out counts as one, because the text could not be verified.</summary>
    private static int CountSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        try
        {
            return SecretScanner.Scan(text).Count;
        }
        catch (RegexMatchTimeoutException)
        {
            return 1;
        }
    }

    /// <summary>The text an item is made from, for the secret scan (the full text: a truncated item may count a little more than is sent).</summary>
    private static string? TextOf(ContextReportItem item, int pinIndex, ChatRequest request, string defaultSystemPrompt, AssembledContext assembled)
    {
        var messages = request.Messages ?? [];
        var conversation = messages.Where(m => m.Role != ChatRole.System).ToList();
        return item.Kind switch
        {
            ContextItemKind.RetrievedChunks => assembled.SentChunks.FirstOrDefault(c => c.Key == item.Key)?.Text,
            ContextItemKind.RepoMap => assembled.Sections.FirstOrDefault(s => s.Kind == PromptSectionKind.RepoMap)?.Text,
            ContextItemKind.SystemPrompt => messages.Any(m => m.Role == ChatRole.System)
                ? string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content))
                : defaultSystemPrompt,
            ContextItemKind.Rules => request.ProjectRules,
            ContextItemKind.Pin when pinIndex >= 0 && request.Pins is { } pins && pinIndex < pins.Length => pins[pinIndex]?.Content,
            ContextItemKind.CurrentFile => request.CurrentFileRegion,
            ContextItemKind.History => string.Join("\n", conversation.Take(Math.Max(0, conversation.Count - 1)).Select(m => m.Content)),
            ContextItemKind.Query => conversation.Count > 0 ? conversation[^1].Content : null,
            _ => null,
        };
    }

    /// <summary>The same target <c>ModelGateway</c> builds for a candidate model.</summary>
    private AssemblyTarget BuildTarget(RoutedModel route, IModelProvider? provider, IReadOnlyList<string> exclude)
    {
        var window = _inventory.Find(route.ProviderId, route.ModelId)?.Capabilities.ContextWindow
            ?? _catalog.GetCapabilities(route.ModelId, route.IsLocal).ContextWindow;
        if (provider is OllamaProvider ollama)
        {
            window = Math.Min(window, ollama.Options.ContextSizeFor(route.Profile));
        }

        return new AssemblyTarget(route.ModelId, window, route.IsLocal, _templates.ChatSystemPrompt, exclude);
    }

    /// <summary>Stands in for the gateway's summariser: calls no model and records the cap it was given.</summary>
    private sealed class StandInSummariser : IHistorySummariser
    {
        public int MaxTokens { get; private set; }

        public Task<string> SummariseAsync(SummariseRequest request, CancellationToken cancellationToken)
        {
            MaxTokens = request.MaxTokens;
            return Task.FromResult($"(A summary of {request.Messages.Count} earlier messages is written when the message is sent.)");
        }
    }
}
