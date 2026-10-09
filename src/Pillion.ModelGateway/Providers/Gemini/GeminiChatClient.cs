using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Providers.Gemini;

/// <summary>
/// Chat over <c>POST /v1beta/models/{model}:streamGenerateContent?alt=sse</c>. System messages go in
/// <c>systemInstruction</c>; the roles are <c>user</c> and <c>model</c>. Thought parts are dropped, and thinking tokens
/// are counted as output tokens. Tool calls are not sent or read in phase 1.
/// </summary>
internal sealed class GeminiChatClient : IChatClient
{
    /// <summary>The most stop sequences the API accepts.</summary>
    private const int MaxStopSequences = 5;

    private readonly GeminiProvider _provider;
    private readonly string _modelId;

    public GeminiChatClient(GeminiProvider provider, string modelId)
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

        var model = GeminiProvider.StripPrefix(string.IsNullOrWhiteSpace(options?.ModelId) ? _modelId : options.ModelId);
        var capabilities = _provider.Catalog.GetCapabilities(model, isLocal: false);
        var body = BuildBody(messages, options, capabilities);
        var path = $"{_provider.ApiVersion}/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse";
        var responseId = Guid.NewGuid().ToString("N");

        long? promptTokens = null;
        long? outputTokens = null;
        long? totalTokens = null;
        ChatFinishReason? finish = null;
        await foreach (var sse in _provider.Http.PostStreamAsync(path, body, model, cancellationToken).ConfigureAwait(false))
        {
            using (sse)
            {
                var root = sse.Data.RootElement;
                var text = new System.Text.StringBuilder();
                if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
                {
                    var candidate = candidates[0];
                    if (candidate.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            var isThought = part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True;
                            if (!isThought && part.TryGetProperty("text", out var piece) && piece.ValueKind == JsonValueKind.String)
                            {
                                text.Append(piece.GetString());
                            }
                        }
                    }

                    if (candidate.TryGetProperty("finishReason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    {
                        finish = MapFinishReason(reason.GetString());
                    }
                }
                else if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out _))
                {
                    // The prompt itself was blocked: no candidates, nothing to stream.
                    finish = ChatFinishReason.ContentFilter;
                }

                // usageMetadata is repeated, cumulatively, in each event; the last one wins.
                if (root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    promptTokens = ReadLong(usage, "promptTokenCount") ?? promptTokens;
                    var candidatesTokens = ReadLong(usage, "candidatesTokenCount");
                    var thoughtsTokens = ReadLong(usage, "thoughtsTokenCount");
                    outputTokens = candidatesTokens is null && thoughtsTokens is null ? outputTokens : (candidatesTokens ?? 0) + (thoughtsTokens ?? 0);
                    totalTokens = ReadLong(usage, "totalTokenCount") ?? totalTokens;
                }

                if (text.Length > 0)
                {
                    var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = model, ResponseId = responseId };
                    update.Contents.Add(new TextContent(text.ToString()));
                    yield return update;
                }
            }
        }

        if (finish is not null || promptTokens is not null || outputTokens is not null)
        {
            var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = model, ResponseId = responseId, FinishReason = finish };
            update.Contents.Add(new UsageContent(new UsageDetails
            {
                InputTokenCount = promptTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = totalTokens ?? (promptTokens is null && outputTokens is null ? null : (promptTokens ?? 0) + (outputTokens ?? 0)),
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
    internal static JsonObject BuildBody(IEnumerable<ChatMessage> messages, ChatOptions? options, ModelCapabilityDescriptor capabilities)
    {
        var system = new List<string>();
        var contents = new JsonArray();
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

            if (ToJson(message) is { } content)
            {
                contents.Add(content);
            }
        }

        var body = new JsonObject { ["contents"] = contents };
        if (system.Count > 0)
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = string.Join("\n\n", system) }),
            };
        }

        var generation = new JsonObject();
        if (options?.MaxOutputTokens is { } maxTokens and > 0)
        {
            generation["maxOutputTokens"] = maxTokens;
        }

        if (options?.Temperature is { } temperature)
        {
            generation["temperature"] = temperature;
        }

        if (options?.StopSequences is { Count: > 0 } stops)
        {
            generation["stopSequences"] = new JsonArray(stops.Take(MaxStopSequences).Select(s => (JsonNode?)s).ToArray());
        }

        if (capabilities.ReasoningEffort && MapThinkingLevel(options?.Reasoning?.Effort) is { } level)
        {
            generation["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = level };
        }

        if (generation.Count > 0)
        {
            body["generationConfig"] = generation;
        }

        return body;
    }

    /// <summary>
    /// Maps the abstraction's reasoning effort to a Gemini 3 <c>thinkingLevel</c> (<c>low</c>, <c>medium</c>, <c>high</c>;
    /// <c>minimal</c> is not offered on every model and is never sent), or <see langword="null"/> to leave the model's default.
    /// </summary>
    internal static string? MapThinkingLevel(ReasoningEffort? effort) => effort switch
    {
        ReasoningEffort.None or ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High or ReasoningEffort.ExtraHigh => "high",
        _ => null,
    };

    internal static ChatFinishReason MapFinishReason(string? reason) => reason switch
    {
        "MAX_TOKENS" => ChatFinishReason.Length,
        "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "LANGUAGE" or "IMAGE_SAFETY" => ChatFinishReason.ContentFilter,
        _ => ChatFinishReason.Stop,
    };

    private static JsonObject? ToJson(ChatMessage message)
    {
        // Tool calls are not used in phase 1, so a tool result is sent as a user turn.
        var role = message.Role == ChatRole.Assistant ? "model" : "user";
        var parts = new JsonArray();
        if (!string.IsNullOrEmpty(message.Text))
        {
            parts.Add(new JsonObject { ["text"] = message.Text });
        }

        foreach (var image in message.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")))
        {
            parts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = image.MediaType,
                    ["data"] = Convert.ToBase64String(image.Data.Span),
                },
            });
        }

        return parts.Count == 0 ? null : new JsonObject { ["role"] = role, ["parts"] = parts };
    }

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
