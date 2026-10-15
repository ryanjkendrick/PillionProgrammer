using System.Collections.Concurrent;
using Pillion.Contracts;
using Pillion.ModelGateway;

namespace Pillion.Eval.Models;

/// <summary>
/// Wraps the real Model Gateway so every document embedding also returns its unit-normalised float32 vector
/// (<see cref="EmbeddingRequest.IncludeFloat32"/>, which the gateway offers for this measurement), and keeps both vectors
/// per input text. The indexing pipeline and the retrieval service run unchanged on top of it: the int8 vectors they
/// receive are the same ones the gateway computes without the flag. The int8-loss measurement then reads the float32 and
/// int8 vector of every chunk without embedding the corpus a second time.
/// </summary>
internal sealed class Float32CapturingGateway : IModelGateway
{
    private readonly IModelGateway _inner;
    private readonly ConcurrentDictionary<string, EmbeddedVector> _documents = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="Float32CapturingGateway"/> class.</summary>
    /// <param name="inner">The real gateway.</param>
    public Float32CapturingGateway(IModelGateway inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>Gets the vectors of every document text embedded so far, keyed by the text the caller sent.</summary>
    public IReadOnlyDictionary<string, EmbeddedVector> Documents => _documents;

    /// <summary>Gets or sets a value indicating whether document vectors are kept (switched off for runs without the int8-loss measurement).</summary>
    public bool Capture { get; set; } = true;

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, TaskClass taskClass, CancellationToken cancellationToken) =>
        _inner.ChatAsync(request, taskClass, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken) =>
        _inner.CompleteAsync(request, cancellationToken);

    /// <inheritdoc/>
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Capture || request.Purpose != EmbeddingPurpose.Document)
        {
            return await _inner.EmbedAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var result = await _inner.EmbedAsync(request with { IncludeFloat32 = true }, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < result.Vectors.Count && i < request.Inputs.Count; i++)
        {
            if (result.Vectors[i] is { Normalized: not null } vector)
            {
                _documents[request.Inputs[i]] = vector;
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken) => _inner.ListModelsAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken) => _inner.TestProviderAsync(providerId, cancellationToken);
}
