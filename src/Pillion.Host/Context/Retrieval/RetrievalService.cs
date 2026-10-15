using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;

namespace Pillion.Host.Context.Retrieval;

/// <summary>Answers the contract's workspace searches (<see cref="IContextService.SearchAsync"/> and <see cref="IContextService.SearchDetailedAsync"/>).</summary>
internal interface IRetrievalService
{
    /// <summary>Searches with hybrid retrieval and reports where the time went.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The hits and timings.</returns>
    Task<SearchResponse> SearchDetailedAsync(SearchRequest request, CancellationToken cancellationToken);

    /// <summary>Searches with hybrid retrieval (contract 1.2 shape: the hits only).</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The hits.</returns>
    Task<SearchResult[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a retriever query over the open indexes of some roots, for the host's own context sources (work item 2.12: chat,
    /// autocomplete and inline edit context). The hits keep the retriever's shape: 1-based lines and the path relative to
    /// the repository root, which is what prompts show.
    /// </summary>
    /// <param name="query">The query (its own time budget, focus files and result count).</param>
    /// <param name="roots">The repository roots; empty means every index the catalog serves.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The response; empty while <c>features.retrieval</c> is off.</returns>
    Task<RetrievalResponse> RetrieveAsync(RetrievalQuery query, IReadOnlyList<string> roots, CancellationToken cancellationToken);
}

/// <summary>
/// The host side of hybrid retrieval (work item 2.11): takes the open indexes from the <see cref="IIndexCatalog"/>, runs
/// <see cref="HybridRetriever"/> over them and maps the hits to the contract (zero-based lines, the opaque chunk id).
/// Behind <c>features.retrieval</c>; <c>features.reranker</c> is honoured but no cross-encoder ships yet (licence review,
/// design section 18), so the fused order is kept either way.
/// </summary>
internal sealed partial class RetrievalService : IRetrievalService
{
    /// <summary>The message of the error returned while the <see cref="FeatureNames.Retrieval"/> feature is off.</summary>
    internal const string RetrievalDisabledMessage =
        "Pillion's workspace search is turned off by settings or policy (feature retrieval). "
        + "Set features.retrieval to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    private readonly IIndexCatalog _catalog;
    private readonly IFeatureFlags _features;
    private readonly HybridRetriever _retriever;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private int _rerankerNoticeLogged;

    /// <summary>Initializes a new instance of the <see cref="RetrievalService"/> class.</summary>
    /// <param name="catalog">Gives the open indexes.</param>
    /// <param name="features">The feature flags.</param>
    /// <param name="embedder">Embeds questions, or <see langword="null"/> when there is no embedding gateway.</param>
    /// <param name="options">Retrieval tuning, or <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public RetrievalService(
        IIndexCatalog catalog,
        IFeatureFlags features,
        IQueryEmbedder? embedder = null,
        RetrievalOptions? options = null,
        TimeProvider? time = null,
        ILogger<RetrievalService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(features);
        _catalog = catalog;
        _features = features;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RetrievalService>.Instance;
        _retriever = new HybridRetriever(embedder, options, NoOpReranker.Instance, _time, _logger);
    }

    /// <summary>Gets the retriever (tests read its query vector cache).</summary>
    internal HybridRetriever Retriever => _retriever;

    /// <inheritdoc/>
    public async Task<SearchResponse> SearchDetailedAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_features.IsEnabled(FeatureNames.Retrieval))
        {
            throw new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = RetrievalDisabledMessage });
        }

        if (_features.IsEnabled(FeatureNames.Reranker) && Interlocked.Exchange(ref _rerankerNoticeLogged, 1) == 0)
        {
            LogNoReranker(_logger);
        }

        var started = _time.GetTimestamp();
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new SearchResponse();
        }

        var response = await RunAsync(ToQuery(request), request.Roots ?? [], cancellationToken).ConfigureAwait(false);

        return new SearchResponse
        {
            Results = [.. response.Hits.Select(ToResult)],
            LexicalMs = response.LexicalMs,
            VectorMs = response.VectorMs,
            FusionMs = response.FusionMs,
            TotalMs = _time.GetElapsedTime(started).TotalMilliseconds,
            Degraded = response.Degraded,
        };
    }

    /// <inheritdoc/>
    public async Task<SearchResult[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken) =>
        (await SearchDetailedAsync(request, cancellationToken).ConfigureAwait(false)).Results;

    /// <inheritdoc/>
    public Task<RetrievalResponse> RetrieveAsync(RetrievalQuery query, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(roots);
        return !_features.IsEnabled(FeatureNames.Retrieval) || string.IsNullOrWhiteSpace(query.Text)
            ? Task.FromResult(RetrievalResponse.Empty)
            : RunAsync(query, roots, cancellationToken);
    }

    private async Task<RetrievalResponse> RunAsync(RetrievalQuery query, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var catalog = await _catalog.GetIndexesAsync(roots, cancellationToken).ConfigureAwait(false);
        if (catalog.Count == 0)
        {
            return RetrievalResponse.Empty;
        }

        var indexes = await Task.WhenAll(catalog.Select(i => StorageRetrievalIndex.CreateAsync(i.IndexId, i.RootPath, i.Storage, i.Graph, cancellationToken))).ConfigureAwait(false);
        return await _retriever.SearchAsync(query, indexes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps a contract request to the retriever's query.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The query.</returns>
    internal static RetrievalQuery ToQuery(SearchRequest request) => new()
    {
        Text = request.Query ?? string.Empty,
        MaxResults = request.MaxResults,
        Mode = request.Mode switch
        {
            SearchMode.Lexical => RetrievalSearchMode.Lexical,
            SearchMode.Vector => RetrievalSearchMode.Vector,
            SearchMode.Symbol => RetrievalSearchMode.Symbol,
            _ => RetrievalSearchMode.Hybrid,
        },
        Partitions = request.Partitions ?? [],
        Languages = request.Languages ?? [],
        MaxPerFile = request.MaxPerFile,
        IncludeText = request.IncludeText,
        FocusFiles = request.FocusFiles ?? [],
    };

    /// <summary>Maps a hit to the contract: zero-based lines, the opaque chunk id, the ranks and scores.</summary>
    /// <param name="hit">The hit.</param>
    /// <returns>The result.</returns>
    internal static SearchResult ToResult(RetrievalHit hit) => new()
    {
        FilePath = hit.FilePath,
        StartLine = Math.Max(0, hit.StartLine - 1),
        EndLine = Math.Max(0, hit.EndLine - 1),
        Snippet = hit.Snippet,
        Score = hit.Score,
        ChunkId = hit.QualifiedChunkId,
        Breadcrumb = hit.Breadcrumb,
        Kind = hit.Kind,
        Partition = hit.Partition,
        Language = hit.Language,
        LexicalRank = hit.LexicalRank,
        VectorRank = hit.VectorRank,
        FusedScore = hit.FusedScore,
        Text = hit.Text,
    };

    [LoggerMessage(EventId = 2930, Level = LogLevel.Information, Message = "features.reranker is on, but no cross-encoder reranker ships yet (it waits on the licence review); the fused order is kept.")]
    private static partial void LogNoReranker(ILogger logger);
}
