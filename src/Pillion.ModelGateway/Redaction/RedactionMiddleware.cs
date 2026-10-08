using System.Runtime.CompilerServices;
using System.Text;
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
            foreach (var text in message.Contents.OfType<TextContent>())
            {
                text.Text = _session.Restore(text.Text ?? string.Empty);
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
            foreach (var text in update.Contents.OfType<TextContent>())
            {
                text.Text = restorer.Push(text.Text ?? string.Empty);
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

    private AIContent RedactContent(AIContent content) =>
        content is TextContent text ? new TextContent(_session.Redact(text.Text ?? string.Empty)) : content;
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
