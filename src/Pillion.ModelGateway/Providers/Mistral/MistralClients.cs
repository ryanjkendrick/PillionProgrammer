using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.OpenAICompatible;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Pillion.ModelGateway.Providers.Mistral;

/// <summary>
/// Chat over <c>POST /chat/completions</c> with <c>stream: true</c>. The body and the chunk parser are the
/// OpenAI-compatible adapter's, except that <c>stream_options</c> is not sent (Mistral's reference does not document it;
/// the final chunk carries <c>usage</c> without it) and <c>max_tokens</c> is the parameter name.
/// </summary>
internal sealed class MistralChatClient : IChatClient
{
    private readonly MistralProvider _provider;
    private readonly string _modelId;

    public MistralChatClient(MistralProvider provider, string modelId)
    {
        _provider = provider;
        _modelId = modelId;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var model = string.IsNullOrWhiteSpace(options?.ModelId) ? _modelId : options.ModelId;
        var body = BuildBody(model, messages, options);
        var responseId = Guid.NewGuid().ToString("N");

        await foreach (var chunk in _provider.Http.PostStreamAsync("chat/completions", body, model, cancellationToken).ConfigureAwait(false))
        {
            using (chunk)
            {
                var update = OpenAIChatClient.ToUpdate(chunk.Data.RootElement, model, responseId);
                if (update is not null)
                {
                    yield return update;
                }
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata(_provider.Id, _provider.Endpoint, _modelId);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // The provider owns the HTTP client.
    }

    /// <summary>Builds the request body: the OpenAI-compatible body without <c>stream_options</c>.</summary>
    internal static JsonObject BuildBody(string model, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var body = OpenAIChatClient.BuildBody(ProviderKind.OpenAiCompatible, model, messages, options);
        body.Remove("stream_options");
        return body;
    }
}

/// <summary>
/// Fill-in-the-middle over <c>POST /fim/completions</c> with <c>prompt</c> and <c>suffix</c>, for Codestral models. The
/// server applies the model's own FIM template, so the prefix and suffix are sent raw. The generated text is read from
/// <c>choices[0].delta.content</c> in stream chunks (also accepting <c>message.content</c> and <c>text</c>).
/// </summary>
internal sealed class MistralFimClient : IFimClient
{
    private readonly MistralProvider _provider;
    private readonly string _modelId;

    public MistralFimClient(MistralProvider provider, string modelId)
    {
        _provider = provider;
        _modelId = modelId;
    }

    public async IAsyncEnumerable<string> CompleteAsync(FimRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = BuildBody(_modelId, request);
        await foreach (var chunk in _provider.Http.PostStreamAsync("fim/completions", body, _modelId, cancellationToken).ConfigureAwait(false))
        {
            using (chunk)
            {
                if (ReadText(chunk.Data.RootElement) is { Length: > 0 } piece)
                {
                    yield return piece;
                }
            }
        }
    }

    internal static JsonObject BuildBody(string model, FimRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["prompt"] = request.Prefix,
            ["suffix"] = request.Suffix,
            ["stream"] = true,
        };

        if (request.MaxTokens > 0)
        {
            body["max_tokens"] = request.MaxTokens;
        }

        if (request.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (request.StopSequences.Count > 0)
        {
            body["stop"] = new JsonArray(request.StopSequences.Select(s => (JsonNode?)s).ToArray());
        }

        return body;
    }

    private static string? ReadText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        foreach (var container in new[] { "delta", "message" })
        {
            if (choice.TryGetProperty(container, out var node) &&
                node.ValueKind == JsonValueKind.Object &&
                node.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }

        return choice.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
    }
}
