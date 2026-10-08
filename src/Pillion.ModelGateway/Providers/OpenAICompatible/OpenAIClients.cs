using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.Common.Settings;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Providers.OpenAICompatible;

/// <summary>
/// Chat over <c>POST /chat/completions</c> with <c>stream: true</c>, parsed from server-sent events. Tool calls are not
/// sent or read in phase 0. Usage is read from the final chunk when the server honours
/// <c>stream_options.include_usage</c>.
/// </summary>
internal sealed class OpenAIChatClient : IChatClient
{
    private readonly OpenAICompatibleProvider _provider;
    private readonly string _modelId;

    public OpenAIChatClient(OpenAICompatibleProvider provider, string modelId)
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
        var body = BuildBody(_provider.Options.Kind, model, messages, options);
        var responseId = Guid.NewGuid().ToString("N");

        await foreach (var chunk in _provider.Http.PostStreamAsync("chat/completions", body, model, cancellationToken).ConfigureAwait(false))
        {
            using (chunk)
            {
                var update = ToUpdate(chunk.RootElement, model, responseId);
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
            return new ChatClientMetadata(_provider.Id, _provider.Options.Endpoint, _modelId);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // The provider owns the HTTP client.
    }

    /// <summary>Builds the request body.</summary>
    internal static JsonObject BuildBody(ProviderKind kind, string model, IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messages.Select(ToJson).ToArray<JsonNode?>()),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };

        var official = kind == ProviderKind.OpenAi;
        if (options?.Temperature is { } temperature && !(official && IsReasoningModel(model)))
        {
            body["temperature"] = temperature;
        }

        if (options?.MaxOutputTokens is { } maxTokens)
        {
            // The official API's newer models only accept max_completion_tokens; compatible servers only know max_tokens.
            body[official ? "max_completion_tokens" : "max_tokens"] = maxTokens;
        }

        if (options?.StopSequences is { Count: > 0 } stops && !(official && IsReasoningModel(model)))
        {
            body["stop"] = new JsonArray(stops.Take(4).Select(s => (JsonNode?)s).ToArray());
        }

        return body;
    }

    /// <summary>
    /// Whether a model of the official API is a reasoning model, which rejects <c>temperature</c> and <c>stop</c>
    /// (the o-series and gpt-5 and later).
    /// </summary>
    internal static bool IsReasoningModel(string model) =>
        model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns one stream chunk (or one whole non-streamed completion) into an update; <see langword="null"/> when it carries nothing.</summary>
    internal static ChatResponseUpdate? ToUpdate(JsonElement root, string model, string responseId)
    {
        string? text = null;
        ChatFinishReason? finish = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if ((choice.TryGetProperty("delta", out var delta) || choice.TryGetProperty("message", out delta)) &&
                delta.ValueKind == JsonValueKind.Object &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString();
            }

            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                finish = reason.GetString() switch
                {
                    "length" => ChatFinishReason.Length,
                    "content_filter" => ChatFinishReason.ContentFilter,
                    "tool_calls" or "function_call" => ChatFinishReason.ToolCalls,
                    _ => ChatFinishReason.Stop,
                };
            }
        }

        UsageDetails? usage = null;
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            usage = new UsageDetails
            {
                InputTokenCount = ReadLong(usageElement, "prompt_tokens"),
                OutputTokenCount = ReadLong(usageElement, "completion_tokens"),
                TotalTokenCount = ReadLong(usageElement, "total_tokens"),
            };
        }

        if (string.IsNullOrEmpty(text) && finish is null && usage is null)
        {
            return null;
        }

        var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = model, ResponseId = responseId, FinishReason = finish };
        if (!string.IsNullOrEmpty(text))
        {
            update.Contents.Add(new TextContent(text));
        }

        if (usage is not null)
        {
            update.Contents.Add(new UsageContent(usage));
        }

        return update;
    }

    private static JsonObject ToJson(ChatMessage message)
    {
        // Tool calls are not used in phase 0, so a tool result is sent as a user message rather than as an invalid tool turn.
        var role = message.Role == ChatRole.System ? "system"
            : message.Role == ChatRole.Assistant ? "assistant"
            : "user";
        var images = message.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")).ToList();
        if (images.Count == 0)
        {
            return new JsonObject { ["role"] = role, ["content"] = message.Text };
        }

        var parts = new JsonArray();
        if (!string.IsNullOrEmpty(message.Text))
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = message.Text });
        }

        foreach (var image in images)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data.Span)}" },
            });
        }

        return new JsonObject { ["role"] = role, ["content"] = parts };
    }

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}

/// <summary>
/// Fill-in-the-middle over <c>POST /completions</c> with <c>suffix</c>, for local servers that implement it (the
/// <c>supportsFim</c> setting). The server applies the model's own FIM template.
/// </summary>
internal sealed class OpenAIFimClient : IFimClient
{
    private readonly OpenAICompatibleProvider _provider;
    private readonly string _modelId;

    public OpenAIFimClient(OpenAICompatibleProvider provider, string modelId)
    {
        _provider = provider;
        _modelId = modelId;
    }

    public async IAsyncEnumerable<string> CompleteAsync(FimRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new JsonObject
        {
            ["model"] = _modelId,
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
            body["stop"] = new JsonArray(request.StopSequences.Take(4).Select(s => (JsonNode?)s).ToArray());
        }

        await foreach (var chunk in _provider.Http.PostStreamAsync("completions", body, _modelId, cancellationToken).ConfigureAwait(false))
        {
            using (chunk)
            {
                if (chunk.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String &&
                    textElement.GetString() is { Length: > 0 } piece)
                {
                    yield return piece;
                }
            }
        }
    }
}
