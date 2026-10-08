using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.Contracts;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Providers.Ollama;

/// <summary>Chat over Ollama's <c>/api/chat</c>, streamed as NDJSON.</summary>
internal sealed class OllamaChatClient : IChatClient
{
    private readonly OllamaProvider _provider;
    private readonly string _modelId;

    public OllamaChatClient(OllamaProvider provider, string modelId)
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
        await _provider.EnsureVersionAsync(cancellationToken).ConfigureAwait(false);

        var model = string.IsNullOrWhiteSpace(options?.ModelId) ? _modelId : options.ModelId;
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messages.Select(ToJson).ToArray<JsonNode?>()),
            ["stream"] = true,
            ["options"] = BuildOptions(_provider.Options, options),
        };
        OllamaProvider.SetKeepAlive(body, KeepAlive(options) ?? _provider.KeepAliveFor(model, isFim: false));

        var responseId = Guid.NewGuid().ToString("N");
        await foreach (var line in _provider.Http.PostStreamAsync("api/chat", body, model, cancellationToken).ConfigureAwait(false))
        {
            using (line)
            {
                var root = line.RootElement;
                var text = root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                    ? content.GetString() ?? string.Empty
                    : string.Empty;
                var update = new ChatResponseUpdate(ChatRole.Assistant, text)
                {
                    ModelId = root.TryGetProperty("model", out var m) ? m.GetString() : model,
                    ResponseId = responseId,
                };

                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                {
                    update.FinishReason = (root.TryGetProperty("done_reason", out var reason) ? reason.GetString() : null) switch
                    {
                        "length" => ChatFinishReason.Length,
                        _ => ChatFinishReason.Stop,
                    };
                    update.Contents.Add(new UsageContent(new UsageDetails
                    {
                        InputTokenCount = ReadLong(root, "prompt_eval_count"),
                        OutputTokenCount = ReadLong(root, "eval_count"),
                    }));
                }

                yield return update;
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
            return new ChatClientMetadata("ollama", _provider.Options.Endpoint, _modelId);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // The provider owns the HTTP client.
    }

    internal static JsonObject BuildOptions(OllamaOptions ollama, ChatOptions? options)
    {
        var profile = options?.AdditionalProperties?.TryGetValue(GatewayOptionKeys.Profile, out var p) == true && p is Profile value ? value : (Profile?)null;
        var contextSize = options?.AdditionalProperties?.TryGetValue(GatewayOptionKeys.ContextSize, out var c) == true && c is int size ? size : ollama.ContextSizeFor(profile);
        var result = new JsonObject { ["num_ctx"] = contextSize };
        if (options?.Temperature is { } temperature)
        {
            result["temperature"] = temperature;
        }

        if (options?.MaxOutputTokens is { } maxTokens)
        {
            result["num_predict"] = maxTokens;
        }

        if (options?.StopSequences is { Count: > 0 } stops)
        {
            result["stop"] = new JsonArray(stops.Select(s => (JsonNode?)s).ToArray());
        }

        return result;
    }

    private static string? KeepAlive(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(GatewayOptionKeys.KeepAlive, out var k) == true ? Convert.ToString(k, CultureInfo.InvariantCulture) : null;

    private static JsonObject ToJson(ChatMessage message)
    {
        var role = message.Role == ChatRole.System ? "system"
            : message.Role == ChatRole.Assistant ? "assistant"
            : message.Role == ChatRole.Tool ? "tool"
            : "user";
        var json = new JsonObject { ["role"] = role, ["content"] = message.Text };
        var images = message.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")).Select(d => (JsonNode?)Convert.ToBase64String(d.Data.Span)).ToArray();
        if (images.Length > 0)
        {
            json["images"] = new JsonArray(images);
        }

        return json;
    }

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
}

/// <summary>Fill-in-the-middle over Ollama's <c>/api/generate</c> with <c>suffix</c> and <c>raw</c> false, so Ollama applies the model's FIM template.</summary>
internal sealed class OllamaFimClient : IFimClient
{
    private readonly OllamaProvider _provider;
    private readonly string _modelId;

    public OllamaFimClient(OllamaProvider provider, string modelId)
    {
        _provider = provider;
        _modelId = modelId;
    }

    public async IAsyncEnumerable<string> CompleteAsync(FimRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _provider.EnsureVersionAsync(cancellationToken).ConfigureAwait(false);

        var options = new JsonObject { ["num_ctx"] = request.ContextSize ?? _provider.Options.ContextSizeFor(request.Profile ?? Profile.LocalFast) };
        if (request.MaxTokens > 0)
        {
            options["num_predict"] = request.MaxTokens;
        }

        if (request.Temperature is { } temperature)
        {
            options["temperature"] = temperature;
        }

        if (request.StopSequences.Count > 0)
        {
            options["stop"] = new JsonArray(request.StopSequences.Select(s => (JsonNode?)s).ToArray());
        }

        var body = new JsonObject
        {
            ["model"] = _modelId,
            ["prompt"] = request.Prefix,
            ["suffix"] = request.Suffix,
            ["raw"] = false,
            ["stream"] = true,
            ["options"] = options,
        };
        OllamaProvider.SetKeepAlive(body, _provider.KeepAliveFor(_modelId, isFim: true));

        await foreach (var line in _provider.Http.PostStreamAsync("api/generate", body, _modelId, cancellationToken).ConfigureAwait(false))
        {
            using (line)
            {
                if (line.RootElement.TryGetProperty("response", out var response) && response.GetString() is { Length: > 0 } text)
                {
                    yield return text;
                }
            }
        }
    }
}
