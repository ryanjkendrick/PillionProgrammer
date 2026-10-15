using Pillion.ContextEngine.Retrieval;
using Pillion.ModelGateway;

namespace Pillion.Host.Context.Retrieval;

/// <summary>
/// Embeds retrieval questions through the Model Gateway (<see cref="IModelGateway.EmbedAsync"/>) with the index's own model
/// and as a query (<see cref="EmbeddingPurpose.Query"/>, so models trained for retrieval get their query instruction). The
/// gateway applies the <c>embeddings</c> flag, routing, Local-only and the Outbound log as for any embedding.
/// </summary>
internal sealed class GatewayQueryEmbedder : IQueryEmbedder
{
    private readonly IModelGateway _gateway;

    /// <summary>Initializes a new instance of the <see cref="GatewayQueryEmbedder"/> class.</summary>
    /// <param name="gateway">The gateway.</param>
    public GatewayQueryEmbedder(IModelGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
    }

    /// <inheritdoc/>
    public async Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dimensions, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var result = await _gateway.EmbedAsync(
            new EmbeddingRequest
            {
                Inputs = [text],
                Purpose = EmbeddingPurpose.Query,
                ModelId = modelId,
                Dimensions = dimensions,
                BatchSize = 1,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Vectors.Count == 0 || result.Vectors[0] is not { } vector)
        {
            var error = result.Failures.Count > 0 ? result.Failures[0].Error : null;
            throw new InvalidOperationException("The query could not be embedded" + (error is null ? "." : ": " + error.Message));
        }

        return new QueryEmbedding(vector.Quantized, result.Model?.QualifiedId ?? modelId ?? string.Empty);
    }
}
