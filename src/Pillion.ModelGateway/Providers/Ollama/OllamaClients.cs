using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Tools;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Providers.Ollama;

/// <summary>
/// Chat over Ollama's <c>/api/chat</c>, streamed as NDJSON. Tools go as <c>tools[].function</c>; the model's calls come
/// back whole in <c>message.tool_calls</c> (arguments as an object, an <c>id</c> on newer servers, else one is made per
/// response), earlier calls go back in the assistant message's <c>tool_calls</c> and results as <c>tool</c> messages with
/// <c>tool_name</c> (work item 3.23). A model without the <c>tools</c> capability answers 400 "does not support tools",
/// which becomes <see cref="ToolsNotSupportedException"/>.
/// </summary>
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
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var tools = ToolsNode(options);

        // One resident configuration per model: the plan names the context the model already has when that is enough, and
        // loads or reloads it (under a timeout, reporting progress) when it is not. The tool definitions count toward the prompt.
        var plan = await _provider.Residency.PrepareAsync(
            new ResidencyRequest(
                model,
                IsFim: false,
                ProfileOf(options),
                ExplicitContext(options),
                OllamaResidency.EstimateNeed(messageList.Select(MessageText).Append(tools?.ToJsonString() ?? string.Empty), options?.MaxOutputTokens),
                KeepAlive(options),
                RequestId(options)),
            cancellationToken).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messageList.SelectMany(m => ToJson(m, messageList)).ToArray<JsonNode?>()),
            ["stream"] = true,
            ["options"] = BuildOptions(plan.NumCtx, options),
        };
        if (tools is not null)
        {
            body["tools"] = tools;
        }

        OllamaProvider.SetKeepAlive(body, plan.KeepAlive);

        var responseId = Guid.NewGuid().ToString("N");
        var callCount = 0;
        var stream = ToolJson.GuardTools(_provider.Http.PostStreamAsync("api/chat", body, model, cancellationToken), tools is not null, model, cancellationToken);
        await foreach (var line in stream.ConfigureAwait(false))
        {
            using (line)
            {
                var root = line.RootElement;
                var hasMessage = root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object;
                var text = hasMessage && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                    ? content.GetString() ?? string.Empty
                    : string.Empty;
                var update = new ChatResponseUpdate(ChatRole.Assistant, text)
                {
                    ModelId = root.TryGetProperty("model", out var m) ? m.GetString() : model,
                    ResponseId = responseId,
                };

                if (hasMessage && message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in toolCalls.EnumerateArray())
                    {
                        update.Contents.Add(ReadToolCall(item, responseId, callCount++, _provider.DisplayName));
                    }
                }

                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                {
                    update.FinishReason = (root.TryGetProperty("done_reason", out var reason) ? reason.GetString() : null) switch
                    {
                        "length" => ChatFinishReason.Length,
                        _ => callCount > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
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

    internal static JsonObject BuildOptions(OllamaOptions ollama, ChatOptions? options) =>
        BuildOptions(ExplicitContext(options) ?? ollama.ContextSizeFor(ProfileOf(options)), options);

    internal static JsonObject BuildOptions(int contextSize, ChatOptions? options)
    {
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

    private static Profile? ProfileOf(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(GatewayOptionKeys.Profile, out var p) == true && p is Profile value ? value : null;

    private static int? ExplicitContext(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(GatewayOptionKeys.ContextSize, out var c) == true && c is int size ? size : null;

    private static string? RequestId(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(GatewayOptionKeys.RequestId, out var r) == true ? r as string : null;

    private static string? KeepAlive(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(GatewayOptionKeys.KeepAlive, out var k) == true ? Convert.ToString(k, CultureInfo.InvariantCulture) : null;

    /// <summary>The <c>tools</c> array for a request, or <see langword="null"/> when it declares none.</summary>
    internal static JsonArray? ToolsNode(ChatOptions? options)
    {
        var tools = ToolJson.Declarations(options);
        return tools.Count == 0
            ? null
            : new JsonArray(tools.Select(t => (JsonNode?)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = ToolJson.Schema(t),
                },
            }).ToArray());
    }

    /// <summary>Reads one entry of <c>message.tool_calls</c>: <c>function.name</c> and <c>function.arguments</c> (an object, or JSON text from some servers).</summary>
    private static FunctionCallContent ReadToolCall(JsonElement item, string responseId, int index, string providerName)
    {
        var function = item.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object ? f : item;
        var name = function.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
        var id = item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(i.GetString())
            ? i.GetString()!
            : ToolJson.NewCallId(name, responseId, index);
        Dictionary<string, object?> arguments;
        string raw;
        if (function.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
        {
            arguments = ToolJson.ToArguments(args, name, providerName);
            raw = args.GetRawText();
        }
        else
        {
            raw = args.ValueKind == JsonValueKind.String ? args.GetString() ?? string.Empty : string.Empty;
            arguments = ToolJson.ParseArguments(raw, name, providerName);
        }

        return new FunctionCallContent(id, name, arguments) { RawRepresentation = raw };
    }

    /// <summary>The text of a message for the context estimate: its text plus the arguments and results it carries.</summary>
    private static string MessageText(ChatMessage message) =>
        string.Concat(message.Contents.Select(c => c switch
        {
            TextContent text => text.Text,
            FunctionCallContent call => ToolJson.ArgumentsJson(call),
            FunctionResultContent result => ToolJson.ResultText(result),
            _ => string.Empty,
        }));

    /// <summary>Converts one message to its wire messages: one for text and tool calls, and one <c>tool</c> message per result.</summary>
    private static IEnumerable<JsonObject> ToJson(ChatMessage message, IReadOnlyList<ChatMessage> conversation)
    {
        var role = message.Role == ChatRole.System ? "system"
            : message.Role == ChatRole.Assistant ? "assistant"
            : message.Role == ChatRole.Tool ? "tool"
            : "user";
        var results = message.Contents.OfType<FunctionResultContent>().ToList();
        var calls = message.Role == ChatRole.Assistant ? message.Contents.OfType<FunctionCallContent>().ToList() : [];
        var images = message.Contents.OfType<DataContent>().Where(d => d.HasTopLevelMediaType("image")).Select(d => (JsonNode?)Convert.ToBase64String(d.Data.Span)).ToArray();

        if (results.Count == 0 || !string.IsNullOrEmpty(message.Text) || images.Length > 0 || calls.Count > 0)
        {
            var json = new JsonObject { ["role"] = results.Count > 0 && role == "tool" ? "user" : role, ["content"] = message.Text };
            if (images.Length > 0)
            {
                json["images"] = new JsonArray(images);
            }

            if (calls.Count > 0)
            {
                json["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode?)new JsonObject
                {
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = ToolJson.ArgumentsNode(c) },
                }).ToArray());
            }

            yield return json;
        }

        foreach (var result in results)
        {
            yield return new JsonObject { ["role"] = "tool", ["content"] = ToolJson.ResultText(result), ["tool_name"] = ToolJson.NameOfCall(conversation, result.CallId) ?? result.CallId };
        }
    }

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
}

/// <summary>Fill-in-the-middle over Ollama's <c>/api/generate</c> with <c>suffix</c> and <c>raw</c> false, so Ollama applies the model's FIM template.</summary>
internal sealed class OllamaFimClient : IFimClient
{
    /// <summary>
    /// The suffix sent when the caret is at the end of the document. Ollama's templates choose fill-in-the-middle only
    /// when the suffix is not empty (<c>{{- if .Suffix }}</c>); with an empty one the qwen2.5-coder template wraps the
    /// prompt in a chat turn and the instruct-tuned model answers it, in prose or behind a Markdown fence (Gate 1 open
    /// issues 1 and 2: typing at the end of a new file). A single line break, the end of a file, keeps the FIM template.
    /// </summary>
    public const string EndOfDocumentSuffix = "\n";

    /// <summary>Gets the suffix to send: the request's, or <see cref="EndOfDocumentSuffix"/> when it is empty.</summary>
    /// <param name="suffix">The request's suffix.</param>
    /// <returns>A suffix Ollama's FIM templates accept.</returns>
    public static string SuffixToSend(string? suffix) => string.IsNullOrEmpty(suffix) ? EndOfDocumentSuffix : suffix;

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

        var plan = await _provider.Residency.PrepareAsync(
            new ResidencyRequest(
                _modelId,
                IsFim: true,
                request.Profile ?? Profile.LocalFast,
                request.ContextSize,
                OllamaResidency.EstimateNeed([request.Prefix, request.Suffix], request.MaxTokens),
                KeepAliveOverride: null,
                request.RequestId),
            cancellationToken).ConfigureAwait(false);

        var options = new JsonObject { ["num_ctx"] = plan.NumCtx };
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
            ["suffix"] = SuffixToSend(request.Suffix),
            ["raw"] = false,
            ["stream"] = true,
            ["options"] = options,
        };
        OllamaProvider.SetKeepAlive(body, plan.KeepAlive);

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

/// <summary>
/// Embeddings over Ollama's <c>/api/embed</c>, which takes a batch of texts and returns one vector per text. The request always
/// carries a small <c>num_ctx</c> (see <see cref="OllamaResidency.PrepareEmbeddingAsync"/>): left to itself Ollama loads the
/// model's whole window, which for qwen3-embedding:0.6b is 5.8 GB instead of 2.2 GB. A text longer than the context is
/// truncated by Ollama (its default), so the chunk cap of the index decides what is read.
/// </summary>
internal sealed class OllamaEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly OllamaProvider _provider;
    private readonly string _modelId;

    public OllamaEmbeddingGenerator(OllamaProvider provider, string modelId)
    {
        _provider = provider;
        _modelId = modelId;
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var inputs = values as IReadOnlyList<string> ?? values.ToList();
        if (inputs.Count == 0)
        {
            return [];
        }

        await _provider.EnsureVersionAsync(cancellationToken).ConfigureAwait(false);

        var model = string.IsNullOrWhiteSpace(options?.ModelId) ? _modelId : options.ModelId;
        var properties = options?.AdditionalProperties;
        var plan = await _provider.Residency.PrepareEmbeddingAsync(
            new ResidencyRequest(
                model,
                IsFim: false,
                Property(properties, GatewayOptionKeys.Profile) is Profile profile ? profile : null,
                Property(properties, GatewayOptionKeys.ContextSize) is int context ? context : null,
                inputs.Max(text => TokenEstimator.Estimate(text)),
                Property(properties, GatewayOptionKeys.KeepAlive) is { } keepAlive ? Convert.ToString(keepAlive, CultureInfo.InvariantCulture) : null,
                Property(properties, GatewayOptionKeys.RequestId) as string,
                IsEmbedding: true),
            cancellationToken).ConfigureAwait(false);

        var runtime = new JsonObject { ["num_ctx"] = plan.NumCtx };
        if (plan.CpuOnly)
        {
            runtime["num_gpu"] = 0;
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(inputs.Select(i => (JsonNode?)i).ToArray()),
            ["options"] = runtime,
        };
        OllamaProvider.SetKeepAlive(body, plan.KeepAlive);
        if (options?.Dimensions is { } dimensions and > 0)
        {
            body["dimensions"] = dimensions;
        }

        using var response = await _provider.Http.PostJsonAsync("api/embed", body, model, cancellationToken).ConfigureAwait(false);
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("embeddings", out var embeddings) || embeddings.ValueKind != JsonValueKind.Array)
        {
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, "Ollama answered the embedding request without embeddings.");
        }

        var result = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        foreach (var vector in embeddings.EnumerateArray())
        {
            result.Add(new Embedding<float>(EmbeddingJson.ReadVector(vector, "Ollama")) { ModelId = model, CreatedAt = DateTimeOffset.UtcNow });
        }

        if (root.TryGetProperty("prompt_eval_count", out var promptTokens) && promptTokens.TryGetInt64(out var tokens))
        {
            result.Usage = new UsageDetails { InputTokenCount = tokens, TotalTokenCount = tokens };
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
            return new EmbeddingGeneratorMetadata("ollama", _provider.Options.Endpoint, _modelId);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // The provider owns the HTTP client.
    }

    private static object? Property(AdditionalPropertiesDictionary? properties, string key) =>
        properties is not null && properties.TryGetValue(key, out var value) ? value : null;
}
