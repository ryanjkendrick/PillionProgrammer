using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>What a text is embedded for. Models trained for retrieval embed a query differently from the passage it should find.</summary>
public enum EmbeddingPurpose
{
    /// <summary>A passage to be found: a chunk of code or documentation that is stored in the index.</summary>
    Document,

    /// <summary>A search query. The catalog may name a prefix or instruction the model expects, and Gemini switches its task type.</summary>
    Query,
}

/// <summary>An embedding request as the gateway receives it: texts in, vectors out, in the same order.</summary>
public sealed record EmbeddingRequest
{
    /// <summary>Gets the id of the request; the Outbound log records each batch as <c>{RequestId}/b{n}</c>.</summary>
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets the texts to embed. None may be empty or <see langword="null"/>.</summary>
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>Gets what the texts are embedded for.</summary>
    public EmbeddingPurpose Purpose { get; init; }

    /// <summary>Gets an explicit model id (<c>providerId/modelId</c> or a bare id) that overrides routing, or <see langword="null"/> to route by profile.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets the profile to route under; <see cref="Profile.Auto"/> lets <c>routing.tasks.embedding</c> decide (local unless it names a cloud profile).</summary>
    public Profile Profile { get; init; } = Profile.Auto;

    /// <summary>
    /// Gets the vector size to ask a model to produce, for models trained to be truncated (OpenAI text-embedding-3, Gemini,
    /// Qwen3-Embedding), or <see langword="null"/> for the model's own size. A model that cannot truncate ignores it.
    /// </summary>
    public int? Dimensions { get; init; }

    /// <summary>Gets the texts per request to the provider; 0 uses <see cref="ModelGatewayOptions.EmbeddingBatchSize"/> (32).</summary>
    public int BatchSize { get; init; }

    /// <summary>
    /// Gets a value indicating whether each result also carries the unit-normalised float32 vector. The int8 vector and its
    /// scale are always returned; the float32 copy is for the recall-loss measurement (work item 2.16) and costs four times the memory.
    /// </summary>
    public bool IncludeFloat32 { get; init; }
}

/// <summary>
/// One embedded text: the unit-normalised vector quantised to int8 with a symmetric scale (<c>value = Int8 * Scale</c>), and
/// optionally the float32 vector it came from.
/// </summary>
/// <param name="Quantized">The int8 vector, <c>round(x / Scale)</c> in -127 to 127.</param>
/// <param name="Scale">The scale of this vector: its largest absolute component divided by 127 (0 for an all-zero vector).</param>
/// <param name="Normalized">The unit-normalised float32 vector the int8 vector was made from, or <see langword="null"/> unless <see cref="EmbeddingRequest.IncludeFloat32"/> was set.</param>
public sealed record EmbeddedVector(sbyte[] Quantized, float Scale, float[]? Normalized)
{
    /// <summary>Gets the number of dimensions.</summary>
    public int Dimensions => Quantized.Length;
}

/// <summary>
/// What the index <c>meta</c> stores to know which model produced its vectors: vectors of different models, or of the same
/// model at a different size, are not comparable, so changing any field means re-embedding.
/// </summary>
/// <param name="ProviderId">The provider that served the model.</param>
/// <param name="ModelId">The provider's model id.</param>
/// <param name="Dimensions">The vector size, read from the first response (not the catalog).</param>
/// <param name="TokenizerFamily">The tokenizer family of the model (catalog data, for example <c>qwen3-bpe</c> or <c>bert-wordpiece</c>); <c>unknown</c> when the catalog does not know the model. Chunk caps are expressed in it.</param>
/// <param name="MaxInputTokens">The most tokens the model reads from one text.</param>
public sealed record EmbeddingModelInfo(string ProviderId, string ModelId, int Dimensions, string TokenizerFamily, int MaxInputTokens)
{
    /// <summary>The value of <see cref="TokenizerFamily"/> for a model the catalog does not describe.</summary>
    public const string UnknownTokenizer = "unknown";

    /// <summary>Gets the qualified model id <c>providerId/modelId</c>.</summary>
    public string QualifiedId => QualifiedModelId.Format(ProviderId, ModelId);
}

/// <summary>A run of inputs that could not be embedded.</summary>
/// <param name="Start">The index of the first input.</param>
/// <param name="Count">The number of inputs.</param>
/// <param name="Error">Why.</param>
/// <param name="Skipped">Whether the batch was never sent because earlier batches kept failing (<see cref="ModelGatewayOptions.EmbeddingMaxConsecutiveFailures"/>).</param>
public sealed record EmbeddingBatchFailure(int Start, int Count, PillionError Error, bool Skipped);

/// <summary>The result of <see cref="IModelGateway.EmbedAsync"/>.</summary>
public sealed class EmbeddingResult
{
    /// <summary>Initializes a new instance of the <see cref="EmbeddingResult"/> class.</summary>
    /// <param name="vectors">One entry per input, in input order; <see langword="null"/> for an input whose batch failed.</param>
    /// <param name="model">The model that produced the vectors, or <see langword="null"/> when there are none.</param>
    /// <param name="failures">The batches that failed.</param>
    /// <param name="batches">The number of requests sent to providers (retries within one batch are not counted).</param>
    /// <param name="promptTokens">The estimated tokens in the inputs that were sent.</param>
    /// <param name="duration">How long the call took.</param>
    public EmbeddingResult(IReadOnlyList<EmbeddedVector?> vectors, EmbeddingModelInfo? model, IReadOnlyList<EmbeddingBatchFailure> failures, int batches, int promptTokens, TimeSpan duration)
    {
        Vectors = vectors;
        Model = model;
        Failures = failures;
        Batches = batches;
        PromptTokens = promptTokens;
        Duration = duration;
    }

    /// <summary>Gets one entry per input, in input order; <see langword="null"/> for an input whose batch failed (see <see cref="Failures"/>).</summary>
    public IReadOnlyList<EmbeddedVector?> Vectors { get; }

    /// <summary>Gets the model that produced the vectors, or <see langword="null"/> when there are none.</summary>
    public EmbeddingModelInfo? Model { get; }

    /// <summary>Gets the batches that failed, in input order.</summary>
    public IReadOnlyList<EmbeddingBatchFailure> Failures { get; }

    /// <summary>Gets the number of batches sent to providers.</summary>
    public int Batches { get; }

    /// <summary>Gets the estimated tokens in the inputs that were sent.</summary>
    public int PromptTokens { get; }

    /// <summary>Gets how long the call took.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Gets a value indicating whether every input has a vector.</summary>
    public bool IsComplete => Failures.Count == 0;
}
