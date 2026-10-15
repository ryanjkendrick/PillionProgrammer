using System.Text.Json;
using System.Text.Json.Serialization;
using Pillion.Contracts;
using Pillion.LocalStore.Conversations;

namespace Pillion.Host.Conversations;

/// <summary>
/// Maps between the contract's conversation types and the Local Store's records, and derives titles. Conventions the wire
/// format cannot express (contract 1.3 has no flag for a stopped reply) are kept here: see <see cref="StoppedMarker"/>.
/// </summary>
internal static class ConversationMapping
{
    /// <summary>The longest title the host derives from a first message, in characters.</summary>
    public const int MaxTitleLength = 60;

    /// <summary>The longest title a rename may set, in characters.</summary>
    public const int MaxRenamedTitleLength = 200;

    /// <summary>
    /// The text <see cref="ConversationMessage.Content"/> ends with when the reply was stopped before it finished (the store keeps
    /// a flag; contract 1.3 has no member for it). The Chat window removes it and shows its own "Stopped." note.
    /// </summary>
    public const string StoppedMarker = "\n\n[Reply stopped before it finished.]";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Makes a title from a first message: its first line that has text, trimmed and cut to <see cref="MaxTitleLength"/> characters.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The title, or an empty string when the message has no visible text.</returns>
    public static string TitleFrom(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        foreach (var raw in message.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            return line.Length <= MaxTitleLength ? line : TrimSurrogate(line, MaxTitleLength).TrimEnd();
        }

        return string.Empty;
    }

    /// <summary>Normalises a title typed by the user: trimmed, one line, at most <see cref="MaxRenamedTitleLength"/> characters.</summary>
    /// <param name="title">The title.</param>
    /// <returns>The title, or an empty string.</returns>
    public static string CleanTitle(string? title)
    {
        var line = (title ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return line.Length <= MaxRenamedTitleLength ? line : TrimSurrogate(line, MaxRenamedTitleLength).TrimEnd();
    }

    /// <summary>Maps a stored conversation to the list item the Chat window shows.</summary>
    /// <param name="record">The conversation.</param>
    /// <param name="messageCount">Its number of messages.</param>
    /// <returns>The summary.</returns>
    public static ConversationSummary ToSummary(ConversationRecord record, int messageCount) => new()
    {
        Id = record.Id,
        Title = record.Title,
        CreatedUtc = record.Created.UtcDateTime,
        UpdatedUtc = record.Updated.UtcDateTime,
        LastModelId = record.Model,
        MessageCount = messageCount,
    };

    /// <summary>Maps a stored message to the contract.</summary>
    /// <param name="record">The message.</param>
    /// <returns>The message.</returns>
    public static ConversationMessage ToMessage(MessageRecord record) => new()
    {
        Id = record.Id,
        Role = ToRole(record.Role),
        Content = record.Cancelled ? record.Content + StoppedMarker : record.Content,
        ModelId = record.Model,
        ProviderId = record.Provider,
        PromptTokens = record.TokensIn ?? 0,
        CompletionTokens = record.TokensOut ?? 0,
        CreatedUtc = record.Created.UtcDateTime,
        Citations = ReadCitations(record.Citations),
    };

    /// <summary>Maps a stored pin to the contract.</summary>
    /// <param name="record">The pin.</param>
    /// <returns>The pin.</returns>
    public static ConversationPin ToPin(PinRecord record) => new()
    {
        Kind = record.Kind,
        Target = record.Target,
        DisplayName = string.IsNullOrEmpty(record.DisplayName) ? DisplayNameFor(record.Kind, record.Target) : record.DisplayName,
        TokenCount = record.TokenCount,
    };

    /// <summary>Maps a pin from the contract to the store.</summary>
    /// <param name="pin">The pin.</param>
    /// <returns>The record.</returns>
    public static PinRecord ToRecord(ConversationPin pin) =>
        new(pin.Kind ?? string.Empty, pin.Target ?? string.Empty, Math.Max(0, pin.TokenCount), pin.DisplayName ?? string.Empty);

    /// <summary>Gets the role a stored message has on the wire.</summary>
    /// <param name="role">The stored role (<c>user</c>, <c>assistant</c>, <c>system</c> or <c>tool</c>).</param>
    /// <returns>The role; an unknown one reads as the assistant.</returns>
    public static ChatRole ToRole(string role) => role switch
    {
        "user" => ChatRole.User,
        "system" => ChatRole.System,
        "tool" => ChatRole.Tool,
        _ => ChatRole.Assistant,
    };

    /// <summary>Serialises citations for storage.</summary>
    /// <param name="citations">The citations.</param>
    /// <returns>JSON, or <see langword="null"/> when there are none.</returns>
    public static string? WriteCitations(Citation[]? citations)
    {
        if (citations is not { Length: > 0 })
        {
            return null;
        }

        return JsonSerializer.Serialize(
            citations.Select(c => new StoredCitation
            {
                Path = c.FilePath,
                Start = c.StartLine,
                End = c.EndLine,
                Chunk = c.ChunkId,
                Breadcrumb = c.Breadcrumb,
                Offset = c.ReplyOffset,
            }),
            JsonOptions);
    }

    /// <summary>Reads stored citations; unreadable JSON gives none rather than failing the conversation.</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The citations.</returns>
    public static Citation[] ReadCitations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredCitation[]>(json, JsonOptions) ?? [];
            return
            [
                .. stored.Select(c => new Citation
                {
                    FilePath = c.Path ?? string.Empty,
                    StartLine = c.Start,
                    EndLine = c.End,
                    ChunkId = c.Chunk ?? string.Empty,
                    Breadcrumb = c.Breadcrumb ?? string.Empty,
                    ReplyOffset = c.Offset ?? -1,
                }),
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string DisplayNameFor(string kind, string target)
    {
        if (kind is MentionKinds.File or MentionKinds.Folder && target.Length > 0)
        {
            var trimmed = target.TrimEnd('\\', '/');
            var cut = trimmed.LastIndexOfAny(['\\', '/']);
            return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        }

        return target.Length == 0 ? "@" + kind : "@" + kind + ":" + target;
    }

    private static string TrimSurrogate(string text, int length)
    {
        // Do not cut a surrogate pair in half.
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private sealed class StoredCitation
    {
        public string? Path { get; set; }

        public int Start { get; set; }

        public int End { get; set; }

        public string? Chunk { get; set; }

        public string? Breadcrumb { get; set; }

        public int? Offset { get; set; }
    }
}
