using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.Tools;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Providers.OpenAICompatible;

/// <summary>
/// Chat over <c>POST /chat/completions</c> with <c>stream: true</c>, parsed from server-sent events. Tools go as
/// <c>tools[].function</c>, earlier calls as <c>assistant.tool_calls</c>, results as <c>tool</c> messages with
/// <c>tool_call_id</c>; streamed <c>delta.tool_calls</c> argument pieces are assembled per <c>index</c> into complete calls
/// (work item 3.23). Usage is read from the final chunk when the server honours <c>stream_options.include_usage</c>.
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
        var calls = new StreamedToolCalls(_provider.Http.DisplayName, responseId);

        var stream = ToolJson.GuardTools(_provider.Http.PostStreamAsync("chat/completions", body, model, cancellationToken), body.ContainsKey("tools"), model, cancellationToken);
        await foreach (var chunk in stream.ConfigureAwait(false))
        {
            using (chunk)
            {
                var update = ToUpdate(chunk.RootElement, model, responseId, calls);
                if (update is not null)
                {
                    yield return update;
                }
            }
        }

        if (calls.HasCalls)
        {
            // The server ended the stream without a finish_reason; the calls it started are still complete.
            yield return CallsUpdate(calls, model, responseId);
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
            ["messages"] = new JsonArray(messages.SelectMany(ToJson).ToArray<JsonNode?>()),
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

        var tools = ToolJson.Declarations(options);
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode?)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = ToolJson.Schema(t),
                },
            }).ToArray());

            if (ToolChoice(options?.ToolMode) is { } choice)
            {
                body["tool_choice"] = choice;
            }
        }

        return body;
    }

    /// <summary>Maps the abstraction's tool mode to <c>tool_choice</c>; <see langword="null"/> leaves the server's default (<c>auto</c>).</summary>
    internal static JsonNode? ToolChoice(ChatToolMode? mode) => mode switch
    {
        NoneChatToolMode => "none",
        RequiredChatToolMode { RequiredFunctionName: { } name } => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name } },
        RequiredChatToolMode => "required",
        _ => null,
    };

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

    /// <summary>
    /// Turns one stream chunk (or one whole non-streamed completion) into an update; <see langword="null"/> when it carries
    /// nothing. Tool-call pieces go to <paramref name="calls"/>; the chunk that carries a <c>finish_reason</c> yields them complete.
    /// </summary>
    internal static ChatResponseUpdate? ToUpdate(JsonElement root, string model, string responseId, StreamedToolCalls calls)
    {
        string? text = null;
        ChatFinishReason? finish = null;
        List<FunctionCallContent>? completed = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if ((choice.TryGetProperty("delta", out var delta) || choice.TryGetProperty("message", out delta)) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString();
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls))
                {
                    calls.Add(toolCalls);
                }
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

                if (calls.HasCalls)
                {
                    completed = calls.Complete();
                    finish = ChatFinishReason.ToolCalls;
                }
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

        if (string.IsNullOrEmpty(text) && finish is null && usage is null && completed is null)
        {
            return null;
        }

        var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = model, ResponseId = responseId, FinishReason = finish };
        if (!string.IsNullOrEmpty(text))
        {
            update.Contents.Add(new TextContent(text));
        }

        if (completed is not null)
        {
            foreach (var call in completed)
            {
                update.Contents.Add(call);
            }
        }

        if (usage is not null)
        {
            update.Contents.Add(new UsageContent(usage));
        }

        return update;
    }

    /// <summary>An update carrying the calls a stream started but never finished with a <c>finish_reason</c>.</summary>
    internal static ChatResponseUpdate CallsUpdate(StreamedToolCalls calls, string model, string responseId)
    {
        var update = new ChatResponseUpdate { Role = ChatRole.Assistant, ModelId = model, ResponseId = responseId, FinishReason = ChatFinishReason.ToolCalls };
        foreach (var call in calls.Complete())
        {
            update.Contents.Add(call);
        }

        return update;
    }

    /// <summary>
    /// Converts one message to its wire messages: one for text (with images and, for the assistant, its <c>tool_calls</c>),
    /// and one <c>tool</c> message per <see cref="FunctionResultContent"/>.
    /// </summary>
    internal static IEnumerable<JsonObject> ToJson(ChatMessage message)
    {
        var role = message.Role == ChatRole.System ? "system"
            : message.Role == ChatRole.Assistant ? "assistant"
            : "user";
        var images = message.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")).ToList();
        var calls = message.Role == ChatRole.Assistant ? message.Contents.OfType<FunctionCallContent>().ToList() : [];
        var results = message.Contents.OfType<FunctionResultContent>().ToList();

        if (results.Count == 0 || !string.IsNullOrEmpty(message.Text) || images.Count > 0 || calls.Count > 0)
        {
            JsonObject json;
            if (images.Count == 0)
            {
                json = new JsonObject { ["role"] = role, ["content"] = calls.Count > 0 && string.IsNullOrEmpty(message.Text) ? null : message.Text };
            }
            else
            {
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

                json = new JsonObject { ["role"] = role, ["content"] = parts };
            }

            if (calls.Count > 0)
            {
                json["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode?)new JsonObject
                {
                    ["id"] = c.CallId,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = ToolJson.ArgumentsJson(c) },
                }).ToArray());
            }

            if (results.Count == 0 || !(json["content"] is null && calls.Count == 0))
            {
                yield return json;
            }
        }

        foreach (var result in results)
        {
            yield return new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.CallId, ["content"] = ToolJson.ResultText(result) };
        }
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
