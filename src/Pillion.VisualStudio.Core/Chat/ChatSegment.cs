using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core;

/// <summary>Whether a <see cref="ChatSegment"/> is prose or a fenced code block.</summary>
public enum ChatSegmentKind
{
    /// <summary>Plain text, shown as wrapped prose.</summary>
    Text,

    /// <summary>The body of a fenced code block, shown in a monospace block.</summary>
    Code,
}

/// <summary>A run of a chat message: plain text or the body of one fenced code block.</summary>
public sealed class ChatSegment : IEquatable<ChatSegment>
{
    /// <summary>Initializes a new instance of the <see cref="ChatSegment"/> class.</summary>
    /// <param name="kind">Text or code.</param>
    /// <param name="text">The text, without the fence lines.</param>
    /// <param name="language">The info string after the opening fence (for example <c>csharp</c>), or empty.</param>
    public ChatSegment(ChatSegmentKind kind, string text, string language = "")
    {
        Kind = kind;
        Text = text ?? string.Empty;
        Language = language ?? string.Empty;
    }

    /// <summary>Gets whether this is text or code.</summary>
    public ChatSegmentKind Kind { get; }

    /// <summary>Gets a value indicating whether this is a code block.</summary>
    public bool IsCode => Kind == ChatSegmentKind.Code;

    /// <summary>Gets the text.</summary>
    public string Text { get; }

    /// <summary>Gets the code block's language, or an empty string.</summary>
    public string Language { get; }

    /// <summary>Gets the name a screen reader announces for the segment.</summary>
    public string AutomationName => IsCode
        ? (Language.Length == 0 ? "Code block" : "Code block, " + Language)
        : "Text";

    /// <inheritdoc/>
    public bool Equals(ChatSegment? other) =>
        other is not null && other.Kind == Kind && string.Equals(other.Text, Text, StringComparison.Ordinal)
            && string.Equals(other.Language, Language, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ChatSegment);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text) ^ (int)Kind;

    /// <inheritdoc/>
    public override string ToString() => Kind + ": " + Text;
}

/// <summary>
/// Splits chat text into prose and fenced code blocks (<c>```</c> or <c>~~~</c>, up to three spaces of indent). This is
/// the whole of phase 0's rendering: no other Markdown is interpreted. An unclosed fence makes the rest of the text
/// code, so a block shows as code while it is still streaming.
/// </summary>
public static class ChatTextSegmenter
{
    /// <summary>Splits text into segments. Empty prose between blocks is dropped.</summary>
    /// <param name="text">The message text.</param>
    /// <returns>The segments, in order.</returns>
    public static IReadOnlyList<ChatSegment> Split(string? text)
    {
        var segments = new List<ChatSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        var lines = text!.Replace("\r\n", "\n").Split('\n');
        var buffer = new List<string>();
        string? fence = null;
        var language = string.Empty;

        foreach (var line in lines)
        {
            if (fence is null)
            {
                if (TryOpenFence(line, out var opening, out var info))
                {
                    AddText(segments, buffer);
                    fence = opening;
                    language = info;
                }
                else
                {
                    buffer.Add(line);
                }
            }
            else if (IsClosingFence(line, fence))
            {
                segments.Add(new ChatSegment(ChatSegmentKind.Code, string.Join("\n", buffer), language));
                buffer.Clear();
                fence = null;
                language = string.Empty;
            }
            else
            {
                buffer.Add(line);
            }
        }

        if (fence is not null)
        {
            segments.Add(new ChatSegment(ChatSegmentKind.Code, string.Join("\n", buffer), language));
        }
        else
        {
            AddText(segments, buffer);
        }

        return segments;
    }

    private static void AddText(List<ChatSegment> segments, List<string> buffer)
    {
        var value = string.Join("\n", buffer).Trim('\n');
        buffer.Clear();
        if (value.Trim().Length > 0)
        {
            segments.Add(new ChatSegment(ChatSegmentKind.Text, value));
        }
    }

    private static bool TryOpenFence(string line, out string fence, out string info)
    {
        fence = string.Empty;
        info = string.Empty;
        var start = CountIndent(line);
        if (start > 3 || start >= line.Length || (line[start] != '`' && line[start] != '~'))
        {
            return false;
        }

        var marker = line[start];
        var end = start;
        while (end < line.Length && line[end] == marker)
        {
            end++;
        }

        if (end - start < 3)
        {
            return false;
        }

        var rest = line.Substring(end).Trim();
        if (marker == '`' && rest.IndexOf('`') >= 0)
        {
            return false; // Inline code such as ```x``` on one line is not a fence.
        }

        fence = line.Substring(start, end - start);
        info = rest.Split(' ', '\t')[0];
        return true;
    }

    private static bool IsClosingFence(string line, string fence)
    {
        var start = CountIndent(line);
        if (start > 3)
        {
            return false;
        }

        var trimmed = line.Substring(start).TrimEnd();
        if (trimmed.Length < fence.Length)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (c != fence[0])
            {
                return false;
            }
        }

        return true;
    }

    private static int CountIndent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }
}
