using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pillion.Common.Redaction;

namespace Pillion.ModelGateway;

/// <summary>
/// Restores placeholders in streamed model output. A placeholder can be split across chunks, so text from a
/// <c>&lt;</c> that could begin <c>&lt;redacted:</c> is held back until the closing <c>&gt;</c> arrives (or until it can no
/// longer be a placeholder), then restored with the request's <see cref="RedactionSession"/>. Not thread-safe; one per stream.
/// </summary>
public sealed class StreamingPlaceholderRestorer
{
    private const string Prefix = "<redacted:";

    // RedactionSession matches <redacted:[^<>\r\n]{0,80}>, so a candidate longer than this can no longer close.
    private const int MaxPlaceholderLength = 10 + 80 + 1;

    private readonly RedactionSession _session;
    private readonly StringBuilder _pending = new();

    /// <summary>Initializes a new instance of the <see cref="StreamingPlaceholderRestorer"/> class.</summary>
    /// <param name="session">The request's redaction session.</param>
    public StreamingPlaceholderRestorer(RedactionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Adds a chunk of output.</summary>
    /// <param name="chunk">The chunk as the model produced it.</param>
    /// <returns>The text that can be emitted now, restored; possibly empty while a placeholder is incomplete.</returns>
    public string Push(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _pending.Append(chunk);
        var text = _pending.ToString();
        var hold = HoldIndex(text);
        _pending.Clear();
        _pending.Append(text, hold, text.Length - hold);
        return hold == 0 ? string.Empty : _session.Restore(text[..hold]);
    }

    /// <summary>Emits whatever is held back at the end of the stream.</summary>
    /// <returns>The remaining text, restored.</returns>
    public string Flush()
    {
        var text = _pending.ToString();
        _pending.Clear();
        return text.Length == 0 ? string.Empty : _session.Restore(text);
    }

    /// <summary>Finds where an incomplete placeholder starts, or returns the text length when nothing needs holding.</summary>
    private static int HoldIndex(string text)
    {
        // Only the last '<' can start an incomplete placeholder: a placeholder cannot contain '<'.
        var start = text.LastIndexOf('<');
        if (start < 0)
        {
            return text.Length;
        }

        var candidate = text.AsSpan(start);
        if (candidate.Length < Prefix.Length)
        {
            return Prefix.AsSpan().StartsWith(candidate, StringComparison.Ordinal) ? start : text.Length;
        }

        if (!candidate.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return text.Length;
        }

        if (candidate.IndexOf('>') >= 0 || candidate.IndexOfAny('\r', '\n') >= 0 || candidate.Length >= MaxPlaceholderLength)
        {
            return text.Length;
        }

        return start;
    }
}

/// <summary>
/// Chat middleware that redacts every outbound message with one <see cref="RedactionSession"/> and restores
/// placeholders in the response, including streamed responses whose placeholders are split across updates. Create one
/// per request; the gateway reads the session's counts for the Outbound log.
/// </summary>
public sealed class RedactingChatClient : DelegatingChatClient
{
    private readonly RedactionSession _session;

    /// <summary>Initializes a new instance of the <see cref="RedactingChatClient"/> class.</summary>
    /// <param name="innerClient">The provider's client.</param>
    /// <param name="session">The request's redaction session; the caller disposes it.</param>
    public RedactingChatClient(IChatClient innerClient, RedactionSession session)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(Redact(messages), options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
        {
            for (var i = 0; i < message.Contents.Count; i++)
            {
                switch (message.Contents[i])
                {
                    case TextContent text:
                        text.Text = _session.Restore(text.Text ?? string.Empty);
                        break;
                    case FunctionCallContent call:
                        message.Contents[i] = RestoreCall(call);
                        break;
                }
            }
        }

        return response;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var restorer = new StreamingPlaceholderRestorer(_session);
        ChatResponseUpdate? last = null;
        await foreach (var update in base.GetStreamingResponseAsync(Redact(messages), options, cancellationToken).ConfigureAwait(false))
        {
            last = update;
            for (var i = 0; i < update.Contents.Count; i++)
            {
                switch (update.Contents[i])
                {
                    case TextContent text:
                        text.Text = restorer.Push(text.Text ?? string.Empty);
                        break;
                    case FunctionCallContent call:
                        // A tool call arrives whole, so its arguments are restored at once: a placeholder the model echoed into a
                        // path or a snippet maps back to the original, exactly as it would in text.
                        update.Contents[i] = RestoreCall(call);
                        break;
                }
            }

            yield return update;
        }

        var rest = restorer.Flush();
        if (rest.Length > 0)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, rest) { ModelId = last?.ModelId };
        }
    }

    private List<ChatMessage> Redact(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            var copy = new ChatMessage(message.Role, message.Contents.Select(RedactContent).ToList())
            {
                AuthorName = message.AuthorName,
                MessageId = message.MessageId,
            };
            result.Add(copy);
        }

        return result;
    }

    /// <summary>
    /// Redacts what carries text: the text itself, the arguments of an earlier tool call (sent back as history) and a tool
    /// result. Tool arguments and results bound for a cloud model pass through the same redaction as text (work item 3.23).
    /// </summary>
    private AIContent RedactContent(AIContent content) => content switch
    {
        TextContent text => new TextContent(_session.Redact(text.Text ?? string.Empty)),
        FunctionCallContent call => new FunctionCallContent(call.CallId, call.Name, RedactArguments(call.Arguments)) { RawRepresentation = call.RawRepresentation },
        FunctionResultContent result => new FunctionResultContent(result.CallId, _session.Redact(ToolResults.Text(result.Result))),
        _ => content,
    };

    private IDictionary<string, object?>? RedactArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return arguments;
        }

        var json = _session.Redact(JsonSerializer.Serialize(arguments));
        return ParseArguments(json) ?? arguments;
    }

    private FunctionCallContent RestoreCall(FunctionCallContent call)
    {
        if (call.Arguments is null || call.Arguments.Count == 0)
        {
            return call;
        }

        var json = JsonSerializer.Serialize(call.Arguments);
        var restored = _session.Restore(json);
        if (string.Equals(json, restored, StringComparison.Ordinal))
        {
            return call;
        }

        return new FunctionCallContent(call.CallId, call.Name, ParseArguments(restored) ?? call.Arguments) { RawRepresentation = restored };
    }

    private static Dictionary<string, object?>? ParseArguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }

            return result;
        }
        catch (JsonException)
        {
            // A placeholder landed where it broke the JSON (inside a key, say); the arguments are left as they were.
            return null;
        }
    }
}

/// <summary>FIM middleware that redacts the prefix and suffix and restores placeholders in the streamed completion.</summary>
public sealed class RedactingFimClient : IFimClient
{
    private readonly IFimClient _inner;
    private readonly RedactionSession _session;

    /// <summary>Initializes a new instance of the <see cref="RedactingFimClient"/> class.</summary>
    /// <param name="inner">The provider's client.</param>
    /// <param name="session">The request's redaction session; the caller disposes it.</param>
    public RedactingFimClient(IFimClient inner, RedactionSession session)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(session);
        _inner = inner;
        _session = session;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> CompleteAsync(FimRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var redacted = request with { Prefix = _session.Redact(request.Prefix), Suffix = _session.Redact(request.Suffix) };
        var restorer = new StreamingPlaceholderRestorer(_session);
        await foreach (var chunk in _inner.CompleteAsync(redacted, cancellationToken).ConfigureAwait(false))
        {
            var text = restorer.Push(chunk);
            if (text.Length > 0)
            {
                yield return text;
            }
        }

        var rest = restorer.Flush();
        if (rest.Length > 0)
        {
            yield return rest;
        }
    }
}
