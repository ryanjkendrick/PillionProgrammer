using System.Text.RegularExpressions;
using Pillion.Common.Redaction;

namespace Pillion.ModelGateway.Editing;

/// <summary>Text helpers shared by the edit-block parser, the matcher and the inline edit prompts.</summary>
public static partial class EditText
{
    /// <summary>Converts CRLF and lone CR line endings to LF. Every editing step works on LF text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with LF line endings.</returns>
    public static string ToLf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains('\r', StringComparison.Ordinal) ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;
    }

    /// <summary>Chooses the line ending a result is written back with: CRLF when the texts use it, else LF.</summary>
    /// <param name="texts">The scope first, then its context; the first text that has a line break decides.</param>
    /// <returns><c>"\r\n"</c> or <c>"\n"</c>.</returns>
    public static string DetectNewline(params string?[] texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var index = text.IndexOf('\n', StringComparison.Ordinal);
            if (index >= 0)
            {
                return index > 0 && text[index - 1] == '\r' ? "\r\n" : "\n";
            }
        }

        return Environment.NewLine == "\r\n" ? "\r\n" : "\n";
    }

    /// <summary>Converts LF text to the given line ending.</summary>
    /// <param name="lfText">Text with LF line endings.</param>
    /// <param name="newline">The line ending to use.</param>
    /// <returns>The converted text.</returns>
    public static string WithNewline(string lfText, string newline)
    {
        ArgumentNullException.ThrowIfNull(lfText);
        ArgumentNullException.ThrowIfNull(newline);
        return newline == "\n" ? lfText : lfText.Replace("\n", newline, StringComparison.Ordinal);
    }

    /// <summary>Gets a Markdown fence longer than any run of backticks in the content (at least three).</summary>
    /// <param name="content">The content to fence.</param>
    /// <returns>The fence.</returns>
    public static string Fence(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var longest = 0;
        var run = 0;
        foreach (var c in content)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    /// <summary>
    /// Finds placeholder-shaped tokens (<c>&lt;redacted:...&gt;</c>) in an edit result that none of the original texts
    /// contain. The gateway restores the placeholders it issued before the text reaches the caller, so any that remain
    /// were invented by the model (or garbled) and cannot be restored: the edit must be rejected (design section 12).
    /// </summary>
    /// <param name="result">The proposed text, after restoration.</param>
    /// <param name="originals">The texts the user already has (the scope, the proposal being refined); a placeholder-shaped token that is really in the code is not an error.</param>
    /// <returns>The distinct unresolvable tokens, in order of first appearance.</returns>
    public static IReadOnlyList<string> FindIntroducedPlaceholders(string result, params string?[] originals)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(originals);

        // A session that issued nothing reports every placeholder-shaped token, with the same shape RedactionSession uses.
        using var empty = new RedactionSession();
        return empty.FindUnresolved(result)
            .Where(token => !originals.Any(o => o is not null && o.Contains(token, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>Splits LF text into lines; a text that ends with a newline has an empty last line, so joining restores it.</summary>
    /// <param name="lfText">Text with LF line endings.</param>
    /// <returns>The lines.</returns>
    public static List<string> SplitLines(string lfText)
    {
        ArgumentNullException.ThrowIfNull(lfText);
        return [.. lfText.Split('\n')];
    }

    /// <summary>Gets the leading whitespace of a line.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The spaces and tabs before the first other character.</returns>
    public static string LeadingWhitespace(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        return line[..i];
    }

    /// <summary>Removes the line-number prefix the inline edit prompt shows (<c>12| </c>) when every non-blank line has one.</summary>
    /// <param name="lines">The lines.</param>
    /// <returns>The lines without the prefixes, or the same lines when not every non-blank line has one.</returns>
    public static IReadOnlyList<string> StripLineNumbers(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var nonBlank = lines.Where(l => l.Trim().Length > 0).ToList();
        if (nonBlank.Count == 0 || !nonBlank.All(l => LineNumberPrefix().IsMatch(l)))
        {
            return lines;
        }

        return lines.Select(l => l.Trim().Length == 0 ? l : LineNumberPrefix().Replace(l, string.Empty, 1)).ToList();
    }

    /// <summary>Formats code with the line-number prefix the inline edit prompt uses.</summary>
    /// <param name="lfText">The code, with LF line endings.</param>
    /// <param name="firstLine">The one-based number of the first line.</param>
    /// <returns>The numbered code.</returns>
    public static string NumberLines(string lfText, int firstLine)
    {
        ArgumentNullException.ThrowIfNull(lfText);
        var lines = SplitLines(lfText);
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var width = (firstLine + lines.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        return string.Join('\n', lines.Select((line, i) =>
            (firstLine + i).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width) + "| " + line));
    }

    [GeneratedRegex(@"^\s*\d+\| ?", RegexOptions.CultureInvariant)]
    private static partial Regex LineNumberPrefix();
}
