using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.Contracts;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Pillion.ModelGateway.Providers.Tools;

/// <summary>
/// The provider refused the tool definitions (an OpenAI-compatible server answering 400 to <c>tools</c>, an Ollama model
/// without the <c>tools</c> capability). The gateway downgrades the model's descriptor to <see cref="ToolCallingSupport.None"/>
/// and the runtime falls back to the text protocol. Never counted against the circuit breaker.
/// </summary>
public sealed class ToolsNotSupportedException : ProviderException
{
    /// <summary>Initializes a new instance of the <see cref="ToolsNotSupportedException"/> class.</summary>
    public ToolsNotSupportedException()
        : this("The model does not support tool calling.", null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ToolsNotSupportedException"/> class.</summary>
    /// <param name="message">A user-facing message.</param>
    public ToolsNotSupportedException(string message)
        : this(message, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ToolsNotSupportedException"/> class.</summary>
    /// <param name="message">A user-facing message.</param>
    /// <param name="innerException">The provider's own error.</param>
    public ToolsNotSupportedException(string message, Exception? innerException)
        : base(PillionErrorCode.ProviderUnavailable, message, (innerException as ProviderException)?.StatusCode ?? HttpStatusCode.BadRequest, innerException, isProviderFault: false)
    {
    }
}

/// <summary>What the adapters share when they carry tools: declarations, argument JSON, result text and the error rules.</summary>
internal static class ToolJson
{
    /// <summary>Gets the function declarations of a request's tools; other kinds of <see cref="AITool"/> are not carried.</summary>
    public static IReadOnlyList<AIFunctionDeclaration> Declarations(ChatOptions? options) =>
        options?.Tools is { Count: > 0 } tools ? tools.OfType<AIFunctionDeclaration>().ToList() : [];

    /// <summary>Gets a declaration's parameter schema as a mutable node (an empty object schema when it has none).</summary>
    public static JsonObject Schema(AIFunctionDeclaration declaration)
    {
        var schema = declaration.JsonSchema;
        if (schema.ValueKind == JsonValueKind.Object && JsonNode.Parse(schema.GetRawText()) is JsonObject node)
        {
            return node;
        }

        return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
    }

    /// <summary>
    /// Removes the JSON-schema keywords Gemini's OpenAPI subset rejects (<c>$schema</c>, <c>$id</c>, <c>$comment</c>,
    /// <c>additionalProperties</c>), at every level.
    /// </summary>
    public static JsonNode? ScrubForGemini(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (key is "$schema" or "$id" or "$comment" or "additionalProperties")
                    {
                        obj.Remove(key);
                    }
                    else
                    {
                        ScrubForGemini(obj[key]);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    ScrubForGemini(item);
                }

                break;
        }

        return node;
    }

    /// <summary>Parses the arguments object of a tool call. Empty text is an empty object (a tool without parameters).</summary>
    /// <param name="json">The arguments as JSON text.</param>
    /// <param name="tool">The tool name, for the message.</param>
    /// <param name="providerName">The provider name, for the message.</param>
    /// <returns>The arguments, each value a <see cref="JsonElement"/>.</returns>
    /// <exception cref="ProviderException">The text is not a JSON object; retryable, because the model may well produce valid JSON next time.</exception>
    public static Dictionary<string, object?> ParseArguments(string? json, string tool, string providerName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ToArguments(document.RootElement, tool, providerName);
        }
        catch (JsonException ex)
        {
            throw Malformed(tool, providerName, ex);
        }
    }

    /// <summary>Converts a parsed arguments object (Ollama and Gemini send one) to the dictionary shape.</summary>
    public static Dictionary<string, object?> ToArguments(JsonElement element, string tool, string providerName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Malformed(tool, providerName, null);
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    /// <summary>The arguments of a call as a JSON object node, for request bodies.</summary>
    public static JsonObject ArgumentsNode(FunctionCallContent call) =>
        JsonNode.Parse(ArgumentsJson(call)) as JsonObject ?? new JsonObject();

    /// <summary>The arguments of a call as JSON text.</summary>
    public static string ArgumentsJson(FunctionCallContent call) =>
        call.Arguments is null ? "{}" : JsonSerializer.Serialize(call.Arguments);

    /// <summary>The UTF-8 size of a call's arguments, for the Outbound log.</summary>
    public static int ArgumentBytes(FunctionCallContent call) => Encoding.UTF8.GetByteCount(ArgumentsJson(call));

    /// <summary>The text of a result as it goes to the model (the gateway has already capped it).</summary>
    public static string ResultText(FunctionResultContent result) => ToolResults.Text(result.Result);

    /// <summary>A call id for providers that send none (Gemini, older Ollama), unique within the response.</summary>
    public static string NewCallId(string name, string responseId, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"call_{responseId}_{index}_{name}");

    /// <summary>Finds the name of the tool a result answers, from the call with the same id earlier in the conversation.</summary>
    public static string? NameOfCall(IEnumerable<ChatMessage> messages, string callId)
    {
        foreach (var message in messages)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                if (string.Equals(call.CallId, callId, StringComparison.Ordinal))
                {
                    return call.Name;
                }
            }
        }

        return null;
    }

    /// <summary>The exception for tool-call arguments that are not valid JSON: retryable, not the provider's fault.</summary>
    public static ProviderException Malformed(string tool, string providerName, Exception? inner) => new(
        PillionErrorCode.ProviderUnavailable,
        $"{providerName} sent arguments for the tool '{tool}' that are not a valid JSON object; the step can be retried.",
        statusCode: null,
        inner,
        isProviderFault: false,
        isRetryable: true);

    /// <summary>
    /// Recognises a request rejected because of its tools: a 400 or 422 whose message names tools or functions (Ollama's
    /// "does not support tools", a compatible server's "unknown parameter: tools", Mistral's "function calling is not enabled").
    /// </summary>
    public static bool IsToolRejection(ProviderException ex)
    {
        if (ex.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity))
        {
            return false;
        }

        var lower = ex.Message.ToLowerInvariant();
        return lower.Contains("tool", StringComparison.Ordinal) || lower.Contains("function", StringComparison.Ordinal);
    }

    /// <summary>
    /// Enumerates a provider stream, turning a rejection of the tools (<see cref="IsToolRejection"/>) into
    /// <see cref="ToolsNotSupportedException"/> when the request carried tools.
    /// </summary>
    public static async IAsyncEnumerable<T> GuardTools<T>(IAsyncEnumerable<T> source, bool hasTools, string modelId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool more;
            try
            {
                more = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (ProviderException ex) when (hasTools && ex is not ToolsNotSupportedException && IsToolRejection(ex))
            {
                throw new ToolsNotSupportedException($"The model '{modelId}' or its server does not accept tool definitions; Pillion will use the text protocol with it.", ex);
            }

            if (!more)
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }
}

/// <summary>
/// Assembles the tool calls of an OpenAI-style stream, whose <c>delta.tool_calls</c> entries carry the id and name once
/// and the arguments in pieces, split anywhere, under an <c>index</c>. One per response.
/// </summary>
internal sealed class StreamedToolCalls
{
    private readonly List<Entry> _entries = [];
    private readonly string _providerName;
    private readonly string _responseId;

    public StreamedToolCalls(string providerName, string responseId)
    {
        _providerName = providerName;
        _responseId = responseId;
    }

    /// <summary>Gets a value indicating whether any call has started.</summary>
    public bool HasCalls => _entries.Count > 0;

    /// <summary>Feeds one <c>tool_calls</c> array from a delta (or a whole non-streamed message).</summary>
    public void Add(JsonElement toolCalls)
    {
        if (toolCalls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in toolCalls.EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) ? n : (int?)null;
            var id = ReadString(item, "id");
            string? name = null;
            string? arguments = null;
            if (item.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                name = ReadString(function, "name");
                arguments = ReadString(function, "arguments");
                if (arguments is null && function.TryGetProperty("arguments", out var parsed) && parsed.ValueKind == JsonValueKind.Object)
                {
                    arguments = parsed.GetRawText(); // Some servers send the object itself.
                }
            }

            // Without an index (Mistral omits it at times) a new id starts a call and a bare fragment continues the last one.
            Entry? entry = index is { } known
                ? _entries.FirstOrDefault(e => e.Index == known)
                : id is not null ? null : _entries.LastOrDefault();
            if (entry is null)
            {
                entry = new Entry(index ?? _entries.Count);
                _entries.Add(entry);
            }

            if (!string.IsNullOrEmpty(id))
            {
                entry.Id = id;
            }

            if (!string.IsNullOrEmpty(name))
            {
                entry.Name = name;
            }

            if (arguments is not null)
            {
                entry.Arguments.Append(arguments);
            }
        }
    }

    /// <summary>Completes every call started so far and forgets them.</summary>
    /// <returns>The calls, in index order.</returns>
    public List<FunctionCallContent> Complete()
    {
        var result = new List<FunctionCallContent>(_entries.Count);
        foreach (var entry in _entries.OrderBy(e => e.Index))
        {
            var name = entry.Name ?? string.Empty;
            var callId = entry.Id ?? ToolJson.NewCallId(name, _responseId, entry.Index);
            var json = entry.Arguments.ToString();
            result.Add(new FunctionCallContent(callId, name, ToolJson.ParseArguments(json, name, _providerName)) { RawRepresentation = json });
        }

        _entries.Clear();
        return result;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class Entry(int index)
    {
        public int Index { get; } = index;

        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
