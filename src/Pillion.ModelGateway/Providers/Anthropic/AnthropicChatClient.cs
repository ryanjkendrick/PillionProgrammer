using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Providers.Anthropic;

/// <summary>
/// Chat over <c>POST /v1/messages</c> with <c>stream: true</c>. System messages go in the top-level <c>system</c> field;
/// <c>max_tokens</c>, which the API requires, is the request's limit or a default capped by the model's maximum output.
/// Sampling parameters are never sent (Claude Opus 4.7 and later reject non-default values). Tool calls are not sent or
/// read in phase 1. Thinking content is read and dropped: only text is streamed to the caller.
/// </summary>
internal sealed class AnthropicChatClient : IChatClient
{
    /// <summary>The <c>max_tokens</c> sent when the request sets none and the model's maximum output allows it. Thinking counts toward it.</summary>
    internal const int DefaultMaxTokens = 8192;

    private readonly AnthropicProvider _provider;
    private readonly string _modelId;

    public AnthropicChatClient(AnthropicProvider provider, string modelId)
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
        var capabilities = _provider.Catalog.GetCapabilities(model, isLocal: false);
        var body = BuildBody(model, messages, options, capabilities);
        var responseId = Guid.NewGuid().ToString("N");

        long? inputTokens = null;
        long? outputTokens = null;
        ChatFinishReason? finish = null;
        var stopped = false;
        await foreach (var sse in _provider.Http.PostStreamAsync("v1/messages", body, model, cancellationToken).ConfigureAwait(false))
        {
            using (sse)
            {
                var root = sse.Data.RootElement;
                switch (ReadString(root, "type"))
                {
                    case "message_start":
                        if (root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var startUsage))
                        {
                            inputTokens = ReadInputTokens(startUsage) ?? inputTokens;
                            outputTokens = ReadLong(startUsage, "output_tokens") ?? outputTokens;
                        }

                        break;

                    case "content_block_start":
                        if (root.TryGetProperty("content_block", out var block) &&
                            ReadString(block, "type") == "text" &&
                            ReadString(block, "text") is { Length: > 0 } initial)
                        {
                            yield return Text(initial, model, responseId);
                        }

                        break;

                    case "content_block_delta":
                        // text_delta carries text; thinking_delta, signature_delta and input_json_delta are not streamed.
                        if (root.TryGetProperty("delta", out var delta) &&
                            ReadString(delta, "type") == "text_delta" &&
                            ReadString(delta, "text") is { Length: > 0 } text)
                        {
                            yield return Text(text, model, responseId);
                        }

                        break;

                    case "message_delta":
                        if (root.TryGetProperty("delta", out var messageDelta) && ReadString(messageDelta, "stop_reason") is { } reason)
                        {
                            finish = MapStopReason(reason);
                        }

                        if (root.TryGetProperty("usage", out var deltaUsage))
                        {
                            // Usage in message_delta is cumulative; input tokens are repeated by newer API versions.
                            inputTokens = ReadInputTokens(deltaUsage) ?? inputTokens;
                            outputTokens = ReadLong(deltaUsage, "output_tokens") ?? outputTokens;
                        }

                        break;

                    case "message_stop":
                        stopped = true;
                        break;
                }
            }

            if (stopped)
            {
                break;
            }
        }

        if (finish is not null || inputTokens is not null || outputTokens is not null)
        {
            var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = model, ResponseId = responseId, FinishReason = finish };
            update.Contents.Add(new UsageContent(new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens is null && outputTokens is null ? null : (inputTokens ?? 0) + (outputTokens ?? 0),
            }));
            yield return update;
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

    /// <summary>Builds the request body.</summary>
    internal static JsonObject BuildBody(string model, IEnumerable<ChatMessage> messages, ChatOptions? options, ModelCapabilityDescriptor capabilities)
    {
        var system = new List<string>();
        var turns = new JsonArray();
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (!string.IsNullOrEmpty(message.Text))
                {
                    system.Add(message.Text);
                }

                continue;
            }

            if (ToJson(message) is { } turn)
            {
                turns.Add(turn);
            }
        }

        var maxTokens = options?.MaxOutputTokens is { } requested and > 0
            ? requested
            : Math.Min(DefaultMaxTokens, capabilities.MaxOutputTokens > 0 ? capabilities.MaxOutputTokens : DefaultMaxTokens);
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["stream"] = true,
            ["messages"] = turns,
        };

        if (system.Count > 0)
        {
            body["system"] = string.Join("\n\n", system);
        }

        if (options?.StopSequences is { Count: > 0 } stops)
        {
            body["stop_sequences"] = new JsonArray(stops.Select(s => (JsonNode?)s).ToArray());
        }

        if (capabilities.ReasoningEffort && MapEffort(options?.Reasoning?.Effort) is { } effort)
        {
            body["output_config"] = new JsonObject { ["effort"] = effort };
        }

        return body;
    }

    /// <summary>Maps the abstraction's reasoning effort to an <c>output_config.effort</c> level, or <see langword="null"/> to leave the model's default.</summary>
    internal static string? MapEffort(ReasoningEffort? effort) => effort switch
    {
        // Thinking cannot be switched off on every current model, so "none" asks for the lowest level.
        ReasoningEffort.None or ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.ExtraHigh => "xhigh",
        _ => null,
    };

    internal static ChatFinishReason MapStopReason(string reason) => reason switch
    {
        "max_tokens" or "model_context_window_exceeded" => ChatFinishReason.Length,
        "refusal" => ChatFinishReason.ContentFilter,
        "tool_use" => ChatFinishReason.ToolCalls,
        _ => ChatFinishReason.Stop,
    };

    private static ChatResponseUpdate Text(string text, string model, string responseId)
    {
        var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = model, ResponseId = responseId };
        update.Contents.Add(new TextContent(text));
        return update;
    }

    private static JsonObject? ToJson(ChatMessage message)
    {
        // Tool calls are not used in phase 1, so a tool result is sent as a user message rather than as an invalid tool turn.
        var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
        var images = message.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")).ToList();
        var text = message.Text;
        if (images.Count == 0)
        {
            // The API rejects empty content.
            return string.IsNullOrWhiteSpace(text) ? null : new JsonObject { ["role"] = role, ["content"] = text };
        }

        var parts = new JsonArray();
        foreach (var image in images)
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = image.MediaType,
                    ["data"] = Convert.ToBase64String(image.Data.Span),
                },
            });
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        return new JsonObject { ["role"] = role, ["content"] = parts };
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? ReadLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    /// <summary>Input tokens including the cached prefix, which the API reports separately.</summary>
    private static long? ReadInputTokens(JsonElement usage)
    {
        var fresh = ReadLong(usage, "input_tokens");
        var created = ReadLong(usage, "cache_creation_input_tokens");
        var read = ReadLong(usage, "cache_read_input_tokens");
        return fresh is null && created is null && read is null ? null : (fresh ?? 0) + (created ?? 0) + (read ?? 0);
    }
}
