using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.ContextEngine.Retrieval;
using Pillion.ContextEngine.Symbols;
using Pillion.Host.WorkspaceQueries;
using Pillion.ModelGateway;

namespace Pillion.Host.Context.Retrieval;

/// <summary>What one retrieval for a prompt found.</summary>
/// <param name="Chunks">The chunks, best first.</param>
/// <param name="Degraded">Whether a leg ran out of time or failed.</param>
/// <param name="TotalMs">How long the search took.</param>
internal sealed record RetrievedCode(IReadOnlyList<ContextChunk> Chunks, bool Degraded, double TotalMs)
{
    /// <summary>Gets the result of a retrieval that did not run.</summary>
    public static RetrievedCode None { get; } = new([], false, 0);
}

/// <summary>An open index's symbol graph and its repository root, for the repo map.</summary>
/// <param name="RootPath">The repository root.</param>
/// <param name="Graph">The graph (paths relative to the root).</param>
internal sealed record RootGraph(string RootPath, SymbolGraph Graph);

/// <summary>
/// The part of hybrid retrieval the host's prompt builders share (work item 2.12): the chat context sources, the autocomplete
/// snippets and the inline edit context all ask it for code. It honours <c>features.retrieval</c>, asks the
/// <see cref="IIndexCatalog"/> whether an index is open, builds queries from text and the identifiers in code, and maps hits
/// to <see cref="ContextChunk"/>s with paths relative to the repository root. A failed search returns nothing: a prompt
/// without retrieved code is better than no answer.
/// </summary>
/// <remarks>
/// <see cref="IRetrievalService"/> is resolved on first use: it embeds questions through the Model Gateway, which itself
/// takes the autocomplete snippet source that uses this class.
/// </remarks>
internal sealed partial class CodeContextRetriever
{
    /// <summary>The most identifiers taken from a piece of code into a query.</summary>
    public const int MaxQueryIdentifiers = 24;

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "async", "await", "base", "bool", "break", "case", "catch", "char", "class", "const", "continue", "decimal",
        "default", "delegate", "double", "else", "enum", "event", "false", "finally", "float", "for", "foreach", "function",
        "get", "if", "import", "in", "int", "interface", "internal", "is", "let", "long", "namespace", "new", "null", "object",
        "out", "override", "private", "protected", "public", "readonly", "ref", "return", "sealed", "set", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "using", "var", "virtual", "void", "while", "def",
        "self", "from", "and", "not", "or", "pass", "none", "None", "True", "False", "End", "Sub", "Dim", "As", "Then",
    };

    private readonly IServiceProvider _services;
    private readonly IIndexCatalog _catalog;
    private readonly IFeatureFlags _features;
    private readonly ISymbolGraphProvider? _graphs;
    private readonly ILogger _logger;
    private IRetrievalService? _retrieval;

    /// <summary>Initializes a new instance of the <see cref="CodeContextRetriever"/> class.</summary>
    /// <param name="services">Resolves the <see cref="IRetrievalService"/> on first use.</param>
    /// <param name="catalog">The open indexes.</param>
    /// <param name="features">The feature flags.</param>
    /// <param name="graphs">The symbol graphs the indexing pipeline supplies, used when the catalog's indexes carry none.</param>
    /// <param name="logger">The logger.</param>
    public CodeContextRetriever(IServiceProvider services, IIndexCatalog catalog, IFeatureFlags features, ISymbolGraphProvider? graphs = null, ILogger<CodeContextRetriever>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(features);
        _services = services;
        _catalog = catalog;
        _features = features;
        _graphs = graphs;
        _logger = logger ?? NullLogger<CodeContextRetriever>.Instance;
    }

    /// <summary>Initializes a new instance of the <see cref="CodeContextRetriever"/> class over a given retrieval service (tests, benchmarks).</summary>
    /// <param name="retrieval">The retrieval service.</param>
    /// <param name="catalog">The open indexes.</param>
    /// <param name="features">The feature flags.</param>
    /// <param name="graphs">The symbol graphs, or <see langword="null"/>.</param>
    internal CodeContextRetriever(IRetrievalService retrieval, IIndexCatalog catalog, IFeatureFlags features, ISymbolGraphProvider? graphs = null)
        : this(new ServiceCollection().BuildServiceProvider(), catalog, features, graphs)
    {
        _retrieval = retrieval;
    }

    /// <summary>Gets a value indicating whether <c>features.retrieval</c> is on.</summary>
    public bool IsEnabled => _features.IsEnabled(FeatureNames.Retrieval);

    /// <summary>Determines whether retrieval is on and at least one index is open.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when a search could find code.</returns>
    public async ValueTask<bool> HasIndexAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return false;
        }

        try
        {
            return (await _catalog.GetIndexesAsync([], cancellationToken).ConfigureAwait(false)).Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(_logger, "listing the indexes", ex.Message);
            return false;
        }
    }

    /// <summary>Retrieves code for a prompt.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="maxResults">The most chunks to return.</param>
    /// <param name="focusFiles">Full paths of the files that get the focus boost, most relevant first.</param>
    /// <param name="timeBudget">The search's time budget, or <see langword="null"/> for the retriever's default.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The chunks; none when retrieval is off, nothing is indexed, or the search failed.</returns>
    public async Task<RetrievedCode> RetrieveAsync(string query, int maxResults, IReadOnlyList<string> focusFiles, TimeSpan? timeBudget, CancellationToken cancellationToken)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return RetrievedCode.None;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            _retrieval ??= _services.GetRequiredService<IRetrievalService>();
            var response = await _retrieval.RetrieveAsync(
                new RetrievalQuery
                {
                    Text = query.Length > 2000 ? query[..2000] : query,
                    MaxResults = maxResults,
                    Mode = RetrievalSearchMode.Hybrid,
                    IncludeText = true,
                    FocusFiles = focusFiles,
                    TimeBudget = timeBudget,
                },
                [],
                cancellationToken).ConfigureAwait(false);
            return new RetrievedCode([.. response.Hits.Select(ToChunk)], response.Degraded, response.TotalMs > 0 ? response.TotalMs : watch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(_logger, "retrieving code", ex.Message);
            return new RetrievedCode([], true, watch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Gets the symbol graphs of the open indexes: the catalog's own, else the ones the graph provider supplies.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The graphs with their roots; empty when none is known.</returns>
    public async Task<IReadOnlyList<RootGraph>> GetGraphsAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return [];
        }

        try
        {
            var indexes = await _catalog.GetIndexesAsync([], cancellationToken).ConfigureAwait(false);
            var result = indexes.Where(i => i.Graph is not null).Select(i => new RootGraph(i.RootPath, i.Graph!)).ToList();
            if (result.Count == 0 && _graphs is not null)
            {
                result.AddRange((await _graphs.GetGraphsAsync(cancellationToken).ConfigureAwait(false)).Select(g => new RootGraph(g.RootPath, g.Graph)));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(_logger, "reading the symbol graphs", ex.Message);
            return [];
        }
    }

    /// <summary>The distinct identifiers of a piece of code, in order of appearance, without keywords and short names.</summary>
    /// <param name="code">The code.</param>
    /// <param name="max">The most identifiers to return.</param>
    /// <returns>The identifiers.</returns>
    public static IReadOnlyList<string> Identifiers(string? code, int max = MaxQueryIdentifiers)
    {
        if (string.IsNullOrEmpty(code) || max <= 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (Match match in Identifier().Matches(code))
        {
            var word = match.Value;
            if (Keywords.Contains(word) || !seen.Add(word))
            {
                continue;
            }

            result.Add(word);
            if (result.Count == max)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Maps a retriever hit to a prompt chunk: the relative path for the prompt, the full path for citations.</summary>
    /// <param name="hit">The hit.</param>
    /// <returns>The chunk.</returns>
    internal static ContextChunk ToChunk(RetrievalHit hit) => new(
        hit.QualifiedChunkId,
        string.IsNullOrEmpty(hit.RelativePath) ? hit.FilePath : hit.RelativePath,
        hit.FilePath,
        Math.Max(1, hit.StartLine),
        Math.Max(hit.StartLine, hit.EndLine),
        hit.Text ?? hit.Snippet,
        hit.Breadcrumb,
        hit.Score);

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [LoggerMessage(EventId = 2940, Level = LogLevel.Information, Message = "Retrieval for a prompt failed while {What}: {Reason}. The prompt goes without retrieved code.")]
    private static partial void LogFailed(ILogger logger, string what, string reason);
}
