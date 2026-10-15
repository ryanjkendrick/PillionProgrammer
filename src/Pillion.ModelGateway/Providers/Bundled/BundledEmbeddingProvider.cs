using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.AI;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Providers.Bundled;

/// <summary>
/// The bundled embedding provider (kind <c>bundled</c>, work item 2.7): the ONNX models in <see cref="BundledModelRuntime"/>,
/// in process and local. It lists only the models whose files are present and verified, embeds and does nothing else (no
/// chat, no fill-in-the-middle). The registry adds it with the id <c>bundled</c> without a settings entry.
/// </summary>
public sealed class BundledEmbeddingProvider : IModelProvider
{
    /// <summary>The id of the provider the registry adds when settings name none of this kind.</summary>
    public const string DefaultId = "bundled";

    /// <summary>The context window reported for a bundled model (as in the catalog): far above any input, because inputs are truncated to <see cref="BundledModelRuntime.EffectiveMaxTokens"/>.</summary>
    public const int TruncatingContextWindow = 1 << 20;

    private readonly BundledModelRuntime _runtime;
    private readonly ConcurrentDictionary<string, BundledEmbeddingGenerator> _generators = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="BundledEmbeddingProvider"/> class.</summary>
    /// <param name="settings">The provider's settings (or the implicit entry).</param>
    /// <param name="runtime">The host's bundled model runtime.</param>
    public BundledEmbeddingProvider(ProviderSettings settings, BundledModelRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtime);

        Id = settings.Id;
        DisplayName = string.IsNullOrWhiteSpace(settings.DisplayName) ? "Bundled models" : settings.DisplayName;
        _runtime = runtime;
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.Bundled;

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <inheritdoc/>
    public bool IsLocal => true;

    /// <summary>Gets the runtime behind the provider.</summary>
    public BundledModelRuntime Runtime => _runtime;

    /// <inheritdoc/>
    public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<ProviderModel>>(
            () => _runtime.GetAvailableModels(cancellationToken).Select(Describe).ToList(),
            cancellationToken);

    /// <inheritdoc/>
    public IChatClient GetChatClient(string modelId) => new NoChatClient(Id, modelId);

    /// <inheritdoc/>
    public IFimClient? GetFimClient(string modelId) => null;

    /// <inheritdoc/>
    public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        if (_generators.TryGetValue(modelId, out var cached))
        {
            return cached;
        }

        // The first lookup hashes the model's files (once per host start); a model that is missing or refused embeds nothing here.
        var located = _runtime.Locate(modelId);
        return located is null ? null : _generators.GetOrAdd(located.Model.Id, id => new BundledEmbeddingGenerator(_runtime, Id, located.Model));
    }

    /// <inheritdoc/>
    public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                var available = _runtime.GetAvailableModels(cancellationToken);
                var message = available.Count == 0
                    ? $"No bundled embedding model is installed or verified. Searched: {string.Join(", ", _runtime.Locator.Roots)}."
                    : $"Bundled models ready: {string.Join(", ", available.Select(m => m.Model.Id))} (in process, no network).";
                return new ProviderProbeResult(available.Count > 0, null, message);
            },
            cancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        // The runtime belongs to the host and outlives settings changes that rebuild this provider.
    }

    private ProviderModel Describe(LocatedModel located)
    {
        var model = located.Model;
        var graph = model.Find(ModelFileRole.Model)!;
        return new ProviderModel(
            Id,
            model.Id,
            model.DisplayName,
            new ModelCapabilityDescriptor
            {
                Chat = false,
                Embeddings = true,
                Streaming = false,
                // Inputs are truncated to the model's limit here, so routing must never reject a long text for its size.
                ContextWindow = TruncatingContextWindow,
                MaxOutputTokens = 0,
                IsLocal = true,
            },
            Digest: "sha256:" + graph.Sha256);
    }

    /// <summary>The chat client of a provider that only embeds: every call fails with a clear error.</summary>
    private sealed class NoChatClient(string providerId, string modelId) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw Error();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw Error();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private ProviderException Error() => new(
            PillionErrorCode.ModelNotInstalled,
            string.Create(CultureInfo.InvariantCulture, $"{QualifiedModelId.Format(providerId, modelId)} is an embedding model; the bundled provider does not chat."),
            isProviderFault: false);
    }
}

/// <summary>
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> over one bundled model. Inference runs on a thread-pool thread with
/// ONNX Runtime's own threads (at most half the cores); the caller's thread is never blocked.
/// </summary>
public sealed class BundledEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly BundledModelRuntime _runtime;
    private readonly ManifestModel _model;
    private readonly EmbeddingGeneratorMetadata _metadata;

    internal BundledEmbeddingGenerator(BundledModelRuntime runtime, string providerId, ManifestModel model)
    {
        _runtime = runtime;
        _model = model;
        _metadata = new EmbeddingGeneratorMetadata(providerId, providerUri: null, model.Id, model.Dimensions);
    }

    /// <inheritdoc/>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var texts = values.ToList();
        if (texts.Count == 0)
        {
            return [];
        }

        var (vectors, tokens) = await Task.Run(() => _runtime.Embed(_model.Id, texts, cancellationToken), cancellationToken).ConfigureAwait(false);
        var created = DateTimeOffset.UtcNow;
        var result = new GeneratedEmbeddings<Embedding<float>>(vectors.Select(v => new Embedding<float>(v) { ModelId = _model.Id, CreatedAt = created }))
        {
            Usage = new UsageDetails { InputTokenCount = tokens, TotalTokenCount = tokens },
        };
        return result;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return _metadata;
        }

        if (serviceType == typeof(IEmbeddingTokenCounter))
        {
            return _runtime.GetTokenCounter(_model.Id);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // The provider and the runtime own the model.
    }
}

/// <summary>
/// Creates the <see cref="BundledEmbeddingProvider"/> over the host's <see cref="BundledModelRuntime"/>. Switched by
/// <see cref="FeatureNames.BundledEmbeddings"/>; needs no API key.
/// </summary>
public sealed class BundledProviderFactory : IModelProviderFactory
{
    private readonly BundledModelRuntime _runtime;

    /// <summary>Initializes a new instance of the <see cref="BundledProviderFactory"/> class.</summary>
    /// <param name="runtime">The host's bundled model runtime.</param>
    public BundledProviderFactory(BundledModelRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.Bundled;

    /// <inheritdoc/>
    public string? FeatureFlag => FeatureNames.BundledEmbeddings;

    /// <inheritdoc/>
    public bool RequiresApiKey(ProviderSettings settings) => false;

    /// <inheritdoc/>
    public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context) => new BundledEmbeddingProvider(settings, _runtime);
}
