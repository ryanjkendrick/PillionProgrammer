using System.Globalization;
using Pillion.ContextEngine.Chunking;
using Pillion.Contracts;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Providers.Bundled;
using TokenCounter = Pillion.ContextEngine.Chunking.TokenCounter;

namespace Pillion.Host.Indexing;

/// <summary>The embedding model an index is built with: its identity, and how its tokenizer counts chunk text.</summary>
/// <param name="ModelId">The qualified model id (<c>providerId/modelId</c>): part of the index identity, so another model builds another index.</param>
/// <param name="TokenCounter">Counts tokens the way the model does (the chunk cap is in these tokens).</param>
/// <param name="MaxTokens">The most tokens the model reads from one text, special tokens included.</param>
/// <param name="TokenizerFamily">The tokenizer family, for the index <c>meta</c>.</param>
internal sealed record IndexEmbeddingModel(string ModelId, TokenCounter TokenCounter, int MaxTokens, string TokenizerFamily);

/// <summary>Turns chunk text into vectors for the index (design section 7, Embed). The gateway in the host; a deterministic fake in tests.</summary>
internal interface IIndexEmbedder
{
    /// <summary>Finds the model the Embedding task class routes to now.</summary>
    /// <returns>The model.</returns>
    /// <exception cref="PillionRpcException">No embedding model can be used (none installed, or the features are off).</exception>
    IndexEmbeddingModel ResolveModel();

    /// <summary>Embeds one batch of texts with a model.</summary>
    /// <param name="model">The model (from <see cref="ResolveModel"/>); every vector of an index comes from it.</param>
    /// <param name="texts">The texts, each at most <see cref="IndexEmbeddingModel.MaxTokens"/> tokens.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One unit-length int8 vector per text, in order.</returns>
    /// <exception cref="PillionRpcException">The batch could not be embedded.</exception>
    Task<IReadOnlyList<EmbeddedVector>> EmbedAsync(IndexEmbeddingModel model, IReadOnlyList<string> texts, CancellationToken cancellationToken);

    /// <summary>Gives back memory the embedder holds between batches (a loaded model's arena); the first step under memory pressure.</summary>
    void ReleaseMemory();
}

/// <summary>
/// The <see cref="IIndexEmbedder"/> over the Model Gateway: the router picks the model for the <see cref="TaskClass.Embedding"/>
/// task class (the bundled ONNX model by default, local only unless the cloud switch and a cloud profile say otherwise, and
/// never anything but local under Local-only), and every batch goes through <see cref="IModelGateway.EmbedAsync"/>, pinned
/// to that model so the vectors of one index stay comparable. The bundled models count tokens with their own tokenizer;
/// other models with the deterministic WordPiece approximation, which errs high.
/// </summary>
internal sealed class GatewayIndexEmbedder : IIndexEmbedder
{
    private readonly IModelRouter _router;
    private readonly IModelGateway _gateway;
    private readonly BundledModelRuntime? _bundled;
    private readonly ModelCatalog _catalog;

    /// <summary>Initializes a new instance of the <see cref="GatewayIndexEmbedder"/> class.</summary>
    /// <param name="router">Chooses the embedding model.</param>
    /// <param name="gateway">Embeds.</param>
    /// <param name="catalog">Describes models the bundled runtime does not.</param>
    /// <param name="bundled">The bundled models' runtime, for their tokenizers.</param>
    public GatewayIndexEmbedder(IModelRouter router, IModelGateway gateway, ModelCatalog catalog, BundledModelRuntime? bundled = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(catalog);
        _router = router;
        _gateway = gateway;
        _catalog = catalog;
        _bundled = bundled;
    }

    /// <inheritdoc/>
    public IndexEmbeddingModel ResolveModel()
    {
        var route = _router.Resolve(new RoutingRequest { TaskClass = TaskClass.Embedding });
        if (string.Equals(route.ProviderId, BundledEmbeddingProvider.DefaultId, StringComparison.OrdinalIgnoreCase)
            && _bundled?.GetTokenCounter(route.ModelId) is { } counter)
        {
            return new IndexEmbeddingModel(route.QualifiedId, text => counter.CountTokens(text.ToString()), counter.MaxTokens, counter.TokenizerFamily);
        }

        var info = _catalog.GetEmbeddingInfo(route.ModelId);
        return new IndexEmbeddingModel(
            route.QualifiedId,
            TokenCounters.ApproximateWordPiece,
            info is { MaxInputTokens: > 0 } ? info.MaxInputTokens : ChunkOptions.DefaultMaxTokens,
            info?.TokenizerFamily ?? EmbeddingModelInfo.UnknownTokenizer);
    }

    /// <inheritdoc/>
    public void ReleaseMemory() => _bundled?.ReleaseUnused();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EmbeddedVector>> EmbedAsync(IndexEmbeddingModel model, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(texts);
        var result = await _gateway.EmbedAsync(
            new EmbeddingRequest
            {
                Inputs = texts,
                Purpose = EmbeddingPurpose.Document,
                ModelId = model.ModelId,
                BatchSize = Math.Max(1, texts.Count),
            },
            cancellationToken).ConfigureAwait(false);

        var vectors = new EmbeddedVector[texts.Count];
        for (var i = 0; i < vectors.Length; i++)
        {
            vectors[i] = result.Vectors[i] ?? throw new PillionRpcException((result.Failures.Count > 0 ? result.Failures[0].Error : null) ?? new PillionError
            {
                Code = PillionErrorCode.ProviderUnavailable,
                Message = string.Create(CultureInfo.InvariantCulture, $"The embedding model {model.ModelId} returned no vector for chunk {i} of the batch."),
            });
        }

        return vectors;
    }
}
