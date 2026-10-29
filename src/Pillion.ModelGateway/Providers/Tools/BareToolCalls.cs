using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Pillion.ModelGateway.Providers.Tools;

/// <summary>
/// Catches a tool call a local model wrote as plain text instead of through the server's tool parser: Qwen2.5-Coder
/// through Ollama sometimes emits the call's JSON (<c>{"name": ..., "arguments": {...}}</c>) without the
/// <c>&lt;tool_call&gt;</c> tags its template asks for, and Ollama then hands it over as ordinary content (seen on the
/// development machine, 2026-10-29). While tools are offered, text that starts a JSON object or a <c>&lt;tool_call&gt;</c>
/// tag is held back until the reply ends; if the whole of it is one or more such objects naming an offered tool, they
/// become <see cref="FunctionCallContent"/>s, otherwise the text is released as it was. Text that starts any other way
/// streams through at once. One per response.
/// </summary>
internal sealed class BareToolCalls
{
    private const string OpenTag = "<tool_call>";
    private const string CloseTag = "</tool_call>";

    private readonly HashSet<string> _names;
    private readonly string _responseId;
    private readonly string _providerName;
    private readonly StringBuilder _held = new();
    private bool _passThrough;

    /// <summary>Initializes a new instance of the <see cref="BareToolCalls"/> class.</summary>
    /// <param name="offered">The names of the tools offered; with none, every text streams through.</param>
    /// <param name="responseId">The response id, for made-up call ids.</param>
    /// <param name="providerName">The provider name, for messages.</param>
    public BareToolCalls(IEnumerable<string> offered, string responseId, string providerName)
    {
        _names = offered.ToHashSet(StringComparer.Ordinal);
        _responseId = responseId;
        _providerName = providerName;
        _passThrough = _names.Count == 0;
    }

    /// <summary>Takes a text delta and returns what can be streamed now (possibly nothing while the text could still be a call).</summary>
    /// <param name="text">The delta.</param>
    /// <returns>The text to emit.</returns>
    public string Push(string text)
    {
        if (_passThrough)
        {
            return text;
        }

        _held.Append(text);
        var trimmed = _held.ToString().TrimStart();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        // A JSON object, a <tool_call> tag or a Markdown fence (```json ... ```) may start a call; anything else is text.
        if (trimmed[0] == '{' || trimmed[0] == '`' || (trimmed.Length <= OpenTag.Length ? OpenTag.StartsWith(trimmed, StringComparison.Ordinal) : trimmed.StartsWith(OpenTag, StringComparison.Ordinal)))
        {
            return string.Empty;
        }

        _passThrough = true;
        var release = _held.ToString();
        _held.Clear();
        return release;
    }

    /// <summary>
    /// Ends the reply: the held text becomes calls when it is nothing but call objects naming offered tools, else it is
    /// returned as text.
    /// </summary>
    /// <returns>The calls found (empty when none) and the text to release (empty when the text became calls).</returns>
    public (IReadOnlyList<FunctionCallContent> Calls, string Text) Finish()
    {
        if (_passThrough || _held.Length == 0)
        {
            var rest = _held.ToString();
            _held.Clear();
            return ([], rest);
        }

        var text = _held.ToString();
        _held.Clear();
        var calls = Parse(text);
        return calls.Count > 0 ? (calls, string.Empty) : ([], text);
    }

    /// <summary>Parses one or more call objects, each optionally wrapped in <c>&lt;tool_call&gt;</c> tags; empty when the text is anything else.</summary>
    private List<FunctionCallContent> Parse(string text)
    {
        var calls = new List<FunctionCallContent>();
        var remaining = StripFence(text.AsSpan().Trim());
        while (remaining.Length > 0)
        {
            if (remaining.StartsWith(OpenTag, StringComparison.Ordinal))
            {
                remaining = remaining[OpenTag.Length..].TrimStart();
            }

            if (remaining.Length == 0 || remaining[0] != '{')
            {
                return [];
            }

            var end = ObjectEnd(remaining);
            if (end < 0)
            {
                return [];
            }

            var call = ToCall(remaining[..(end + 1)].ToString(), calls.Count);
            if (call is null)
            {
                return [];
            }

            calls.Add(call);
            remaining = remaining[(end + 1)..].TrimStart();
            if (remaining.StartsWith(CloseTag, StringComparison.Ordinal))
            {
                remaining = remaining[CloseTag.Length..].TrimStart();
            }
        }

        return calls;
    }

    /// <summary>Removes a Markdown fence around the whole text (<c>```json</c> ... <c>```</c>), which some models wrap a call in.</summary>
    private static ReadOnlySpan<char> StripFence(ReadOnlySpan<char> text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal) || !text.EndsWith("```", StringComparison.Ordinal) || text.Length < 6)
        {
            return text;
        }

        var inner = text[3..^3];
        var newline = inner.IndexOfAny('\r', '\n');
        if (newline < 0)
        {
            return text;
        }

        // The language word after the opening fence, if any, is letters only.
        foreach (var c in inner[..newline].Trim())
        {
            if (!char.IsLetter(c))
            {
                return text;
            }
        }

        return inner[newline..].Trim();
    }

    /// <summary>The index of the brace that closes the object at the start of <paramref name="text"/>, or -1.</summary>
    private static int ObjectEnd(ReadOnlySpan<char> text)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private FunctionCallContent? ToCall(string json, int index)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                nameElement.GetString() is not { } name || !_names.Contains(name))
            {
                return null;
            }

            Dictionary<string, object?> arguments;
            string raw;
            if (root.TryGetProperty("arguments", out var args) || root.TryGetProperty("parameters", out args))
            {
                if (args.ValueKind == JsonValueKind.Object)
                {
                    arguments = ToolJson.ToArguments(args, name, _providerName);
                    raw = args.GetRawText();
                }
                else if (args.ValueKind == JsonValueKind.String)
                {
                    raw = args.GetString() ?? string.Empty;
                    arguments = ToolJson.ParseArguments(raw, name, _providerName);
                }
                else
                {
                    return null;
                }
            }
            else
            {
                arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
                raw = "{}";
            }

            return new FunctionCallContent(ToolJson.NewCallId(name, _responseId, index), name, arguments) { RawRepresentation = raw };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ProviderException)
        {
            // Arguments that are not an object: not a call the runtime could act on, so it stays text.
            return null;
        }
    }
}
