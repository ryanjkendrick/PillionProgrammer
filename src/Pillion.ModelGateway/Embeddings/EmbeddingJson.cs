using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>JSON reading shared by the embedding adapters.</summary>
internal static class EmbeddingJson
{
    /// <summary>Reads a JSON array of numbers into a vector.</summary>
    /// <param name="element">The array.</param>
    /// <param name="providerName">The provider name for the message.</param>
    /// <returns>The components.</returns>
    /// <exception cref="ProviderException">The element is not an array of numbers.</exception>
    public static float[] ReadVector(JsonElement element, string providerName)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Unreadable(providerName);
        }

        var vector = new float[element.GetArrayLength()];
        var index = 0;
        foreach (var number in element.EnumerateArray())
        {
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetSingle(out var value))
            {
                throw Unreadable(providerName);
            }

            vector[index++] = value;
        }

        return vector;
    }

    /// <summary>Creates the error for an embedding response that is not in the expected shape.</summary>
    /// <param name="providerName">The provider name for the message.</param>
    /// <returns>The exception.</returns>
    public static ProviderException Unreadable(string providerName) =>
        new(PillionErrorCode.ProviderUnavailable, $"{providerName} sent an embedding response Pillion could not read.");
}

/// <summary>
/// An embedding generator for the OpenAI-shaped <c>POST …/embeddings</c> that OpenAI, LM Studio, the llama.cpp server, vLLM,
/// OpenRouter, Azure OpenAI and Mistral all serve: <c>{ "model", "input": [...], "dimensions" }</c> in, <c>{ "data": [{ "index",
/// "embedding" }], "usage" }</c> out. The caller supplies the HTTP call, because each provider authenticates and addresses it differently.
/// </summary>
internal sealed class OpenAIShapedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly string _modelId;
    private readonly string _providerName;
    private readonly EmbeddingGeneratorMetadata _metadata;
    private readonly bool _sendModel;
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonDocument>> _post;

    /// <summary>Initializes a new instance of the <see cref="OpenAIShapedEmbeddingGenerator"/> class.</summary>
    /// <param name="modelId">The model (for Azure OpenAI, the deployment).</param>
    /// <param name="providerName">The provider name for messages and metadata.</param>
    /// <param name="endpoint">The endpoint, for metadata.</param>
    /// <param name="sendModel">Whether the body names the model (Azure addresses the deployment in the path instead).</param>
    /// <param name="post">Sends the body (model id, body, token) and returns the parsed response.</param>
    public OpenAIShapedEmbeddingGenerator(string modelId, string providerName, Uri? endpoint, bool sendModel, Func<string, JsonObject, CancellationToken, Task<JsonDocument>> post)
    {
        _modelId = modelId;
        _providerName = providerName;
        _metadata = new EmbeddingGeneratorMetadata(providerName, endpoint, modelId);
        _sendModel = sendModel;
        _post = post;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var inputs = values as IReadOnlyList<string> ?? values.ToList();
        if (inputs.Count == 0)
        {
            return [];
        }

        var model = string.IsNullOrWhiteSpace(options?.ModelId) ? _modelId : options.ModelId;
        var body = new JsonObject();
        if (_sendModel)
        {
            body["model"] = model;
        }

        body["input"] = new JsonArray(inputs.Select(i => (JsonNode?)i).ToArray());
        if (options?.Dimensions is { } dimensions and > 0)
        {
            body["dimensions"] = dimensions;
        }

        using var response = await _post(model, body, cancellationToken).ConfigureAwait(false);
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw EmbeddingJson.Unreadable(_providerName);
        }

        // Servers return the items in order, but the documented contract is the index; honour it when present.
        var slots = new float[inputs.Count][];
        var next = 0;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("embedding", out var embedding))
            {
                throw EmbeddingJson.Unreadable(_providerName);
            }

            var index = item.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var parsed) ? parsed : next;
            if (index < 0 || index >= slots.Length || slots[index] is not null)
            {
                throw EmbeddingJson.Unreadable(_providerName);
            }

            slots[index] = EmbeddingJson.ReadVector(embedding, _providerName);
            next++;
        }

        var result = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        foreach (var slot in slots)
        {
            result.Add(new Embedding<float>(slot ?? throw EmbeddingJson.Unreadable(_providerName)) { ModelId = model, CreatedAt = DateTimeOffset.UtcNow });
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            long? prompt = usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt64(out var pt) ? pt : null;
            long? total = usage.TryGetProperty("total_tokens", out var t) && t.TryGetInt64(out var tt) ? tt : prompt;
            if (prompt is not null || total is not null)
            {
                result.Usage = new UsageDetails { InputTokenCount = prompt ?? total, TotalTokenCount = total };
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
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // The provider owns the HTTP client.
    }
}
