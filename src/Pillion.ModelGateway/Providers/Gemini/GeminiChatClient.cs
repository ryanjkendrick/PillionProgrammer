using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.ModelGateway.Providers.Tools;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Providers.Gemini;

/// <summary>
/// Chat over <c>POST /v1beta/models/{model}:streamGenerateContent?alt=sse</c>. System messages go in
/// <c>systemInstruction</c>; the roles are <c>user</c> and <c>model</c>. Thought parts are dropped, and thinking tokens
/// are counted as output tokens. Tools go as <c>tools[].functionDeclarations</c>, the model's calls come back as whole
/// <c>functionCall</c> parts (Gemini streams no argument deltas and no call id, so one is made per response), earlier calls
/// go back as <c>functionCall</c> parts of a <c>model</c> turn and results as <c>functionResponse</c> parts of a <c>user</c>
/// turn, named after the call they answer (work item 3.23).
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
        var callCount = 0;
        var stream = ToolJson.GuardTools(_provider.Http.PostStreamAsync(path, body, model, cancellationToken), body.ContainsKey("tools"), model, cancellationToken);
        await foreach (var sse in stream.ConfigureAwait(false))
        {
            using (sse)
            {
                var root = sse.Data.RootElement;
                var text = new System.Text.StringBuilder();
                var calls = new List<FunctionCallContent>();
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
                            else if (part.TryGetProperty("functionCall", out var functionCall) && functionCall.ValueKind == JsonValueKind.Object)
                            {
                                var name = functionCall.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
                                var id = functionCall.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(i.GetString())
                                    ? i.GetString()!
                                    : ToolJson.NewCallId(name, responseId, callCount);
                                callCount++;
                                var arguments = functionCall.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object
                                    ? ToolJson.ToArguments(args, name, _provider.DisplayName)
                                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                                calls.Add(new FunctionCallContent(id, name, arguments) { RawRepresentation = args.ValueKind == JsonValueKind.Object ? args.GetRawText() : "{}" });
                            }
                        }
                    }

                    if (candidate.TryGetProperty("finishReason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    {
                        finish = MapFinishReason(reason.GetString());
                    }
                }

                if (calls.Count > 0)
                {
                    var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = model, ResponseId = responseId };
                    foreach (var call in calls)
                    {
                        update.Contents.Add(call);
                    }

                    yield return update;
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

        if (callCount > 0 && finish != ChatFinishReason.Length && finish != ChatFinishReason.ContentFilter)
        {
            // Gemini reports STOP after a function call; the abstraction says ToolCalls.
            finish = ChatFinishReason.ToolCalls;
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
        var all = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        foreach (var message in all)
        {
            if (message.Role == ChatRole.System)
            {
                if (!string.IsNullOrEmpty(message.Text))
                {
                    system.Add(message.Text);
                }

                continue;
            }

            if (ToJson(message, all) is { } content)
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

        var tools = ToolJson.Declarations(options);
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(tools.Select(t => (JsonNode?)new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = ToolJson.ScrubForGemini(ToolJson.Schema(t)),
                }).ToArray()),
            });

            if (FunctionCallingConfig(options?.ToolMode) is { } config)
            {
                body["toolConfig"] = new JsonObject { ["functionCallingConfig"] = config };
            }
        }

        return body;
    }

    /// <summary>Maps the abstraction's tool mode to <c>toolConfig.functionCallingConfig</c>; <see langword="null"/> leaves the API's default (<c>AUTO</c>).</summary>
    internal static JsonObject? FunctionCallingConfig(ChatToolMode? mode) => mode switch
    {
        NoneChatToolMode => new JsonObject { ["mode"] = "NONE" },
        RequiredChatToolMode { RequiredFunctionName: { } name } => new JsonObject { ["mode"] = "ANY", ["allowedFunctionNames"] = new JsonArray(name) },
        RequiredChatToolMode => new JsonObject { ["mode"] = "ANY" },
        _ => null,
    };

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

    private static JsonObject? ToJson(ChatMessage message, IReadOnlyList<ChatMessage> conversation)
    {
        var role = message.Role == ChatRole.Assistant ? "model" : "user";
        var parts = new JsonArray();
        foreach (var result in message.Contents.OfType<FunctionResultContent>())
        {
            // Gemini keys a response by function name, not by call id; the name comes from the call this result answers.
            parts.Add(new JsonObject
            {
                ["functionResponse"] = new JsonObject
                {
                    ["name"] = ToolJson.NameOfCall(conversation, result.CallId) ?? result.CallId,
                    ["response"] = new JsonObject { ["result"] = ToolJson.ResultText(result) },
                },
            });
        }

        if (!string.IsNullOrEmpty(message.Text))
        {
            parts.Add(new JsonObject { ["text"] = message.Text });
        }

        if (message.Role == ChatRole.Assistant)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                parts.Add(new JsonObject { ["functionCall"] = new JsonObject { ["name"] = call.Name, ["args"] = ToolJson.ArgumentsNode(call) } });
            }
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
