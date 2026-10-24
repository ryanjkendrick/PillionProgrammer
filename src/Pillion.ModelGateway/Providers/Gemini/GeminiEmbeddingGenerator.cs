using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Pillion.ModelGateway.Providers.Gemini;

/// <summary>
/// Embeddings over the Gemini API: <c>POST /v1beta/models/{model}:embedContent</c> for one text and
/// <c>:batchEmbedContents</c> for several (at most <see cref="MaxBatch"/> per request, the API's limit; a larger call is split).
/// The task type follows <see cref="GatewayOptionKeys.EmbeddingPurpose"/>: <c>RETRIEVAL_DOCUMENT</c> for passages and
/// <c>RETRIEVAL_QUERY</c> for search queries. <see cref="EmbeddingGenerationOptions.Dimensions"/> becomes <c>outputDimensionality</c>.
/// </summary>
internal sealed class GeminiEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The most texts <c>batchEmbedContents</c> accepts in one request.</summary>
    public const int MaxBatch = 100;

    private readonly GeminiProvider _provider;
    private readonly string _modelId;

    public GeminiEmbeddingGenerator(GeminiProvider provider, string modelId)
    {
        _provider = provider;
        _modelId = modelId;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var inputs = values as IReadOnlyList<string> ?? values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        if (inputs.Count == 0)
        {
            return result;
        }

        var model = GeminiProvider.StripPrefix(string.IsNullOrWhiteSpace(options?.ModelId) ? _modelId : options.ModelId);
        var taskType = TaskTypeOf(options);
        var dimensions = options?.Dimensions is { } d and > 0 ? d : (int?)null;

        for (var start = 0; start < inputs.Count; start += MaxBatch)
        {
            var count = Math.Min(MaxBatch, inputs.Count - start);
            List<float[]> vectors;
            if (count == 1)
            {
                vectors = [await EmbedOneAsync(model, inputs[start], taskType, dimensions, cancellationToken).ConfigureAwait(false)];
            }
            else
            {
                vectors = await EmbedManyAsync(model, inputs.Skip(start).Take(count).ToList(), taskType, dimensions, cancellationToken).ConfigureAwait(false);
            }

            foreach (var vector in vectors)
            {
                result.Add(new Embedding<float>(vector) { ModelId = model, CreatedAt = DateTimeOffset.UtcNow });
            }
        }

        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return new EmbeddingGeneratorMetadata("gemini", _provider.Endpoint, _modelId);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // The provider owns the HTTP client.
    }

    private static string? TaskTypeOf(EmbeddingGenerationOptions? options) =>
        options?.AdditionalProperties is { } properties && properties.TryGetValue(GatewayOptionKeys.EmbeddingPurpose, out var purpose)
            ? purpose switch
            {
                EmbeddingPurpose.Query => "RETRIEVAL_QUERY",
                EmbeddingPurpose.Document => "RETRIEVAL_DOCUMENT",
                _ => null,
            }
            : null;

    private static JsonObject BuildRequest(string model, string text, string? taskType, int? dimensions)
    {
        var request = new JsonObject
        {
            ["model"] = "models/" + model,
            ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
        };
        if (taskType is not null)
        {
            request["taskType"] = taskType;
        }

        if (dimensions is { } size)
        {
            request["outputDimensionality"] = size;
        }

        return request;
    }

    private async Task<float[]> EmbedOneAsync(string model, string text, string? taskType, int? dimensions, CancellationToken cancellationToken)
    {
        using var response = await _provider.Http.PostJsonAsync(
            $"{_provider.ApiVersion}/models/{Uri.EscapeDataString(model)}:embedContent",
            BuildRequest(model, text, taskType, dimensions),
            model,
            cancellationToken).ConfigureAwait(false);
        if (response.RootElement.ValueKind != JsonValueKind.Object ||
            !response.RootElement.TryGetProperty("embedding", out var embedding) ||
            embedding.ValueKind != JsonValueKind.Object ||
            !embedding.TryGetProperty("values", out var values))
        {
            throw EmbeddingJson.Unreadable(_provider.DisplayName);
        }

        return EmbeddingJson.ReadVector(values, _provider.DisplayName);
    }

    private async Task<List<float[]>> EmbedManyAsync(string model, List<string> texts, string? taskType, int? dimensions, CancellationToken cancellationToken)
    {
        var requests = new JsonArray();
        foreach (var text in texts)
        {
            requests.Add(BuildRequest(model, text, taskType, dimensions));
        }

        using var response = await _provider.Http.PostJsonAsync(
            $"{_provider.ApiVersion}/models/{Uri.EscapeDataString(model)}:batchEmbedContents",
            new JsonObject { ["requests"] = requests },
            model,
            cancellationToken).ConfigureAwait(false);
        if (response.RootElement.ValueKind != JsonValueKind.Object ||
            !response.RootElement.TryGetProperty("embeddings", out var embeddings) ||
            embeddings.ValueKind != JsonValueKind.Array ||
            embeddings.GetArrayLength() != texts.Count)
        {
            throw EmbeddingJson.Unreadable(_provider.DisplayName);
        }

        var vectors = new List<float[]>(texts.Count);
        foreach (var embedding in embeddings.EnumerateArray())
        {
            if (embedding.ValueKind != JsonValueKind.Object || !embedding.TryGetProperty("values", out var values))
            {
                throw EmbeddingJson.Unreadable(_provider.DisplayName);
            }

            vectors.Add(EmbeddingJson.ReadVector(values, _provider.DisplayName));
        }

        return vectors;
    }
}
