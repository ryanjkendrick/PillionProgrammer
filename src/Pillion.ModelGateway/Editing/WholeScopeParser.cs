using System.Text.RegularExpressions;

namespace Pillion.ModelGateway.Editing;

/// <summary>The result of reading a whole-scope reply.</summary>
/// <param name="Code">The rewritten code (LF line endings), or <see langword="null"/> when the reply held no usable code.</param>
/// <param name="Explanation">The prose around a fenced code block, trimmed; empty when there is none.</param>
/// <param name="Problem">When <paramref name="Code"/> is <see langword="null"/>: what is wrong with the reply, for the log and the error.</param>
public sealed record WholeScopeReply(string? Code, string Explanation, string? Problem);

/// <summary>
/// Reads a model's reply to the whole-scope inline edit prompt (<c>Templates/inline-edit.json</c>, <c>system.editWhole</c>):
/// the complete rewritten code as plain text. It is tolerant of what models do anyway: wrapping the code in a Markdown
/// fence (with prose before or after it), echoing the <c>N| </c> line numbers of the prompt on every line, CRLF line
/// endings, and blank lines around the code. The trailing newline of the result follows the code being replaced.
/// </summary>
public static partial class WholeScopeParser
{
    /// <summary>Counts the lines of a text the way an editor shows them: a final line break does not start another line.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The number of lines; 0 for an empty text.</returns>
    public static int CountLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return 0;
        }

        var lf = EditText.ToLf(text);
        var count = 1;
        for (var i = 0; i < lf.Length - 1; i++)
        {
            if (lf[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Reads a complete reply.</summary>
    /// <param name="reply">The reply text.</param>
    /// <param name="baseCode">The code the reply rewrites (LF); decides the trailing newline and whether fences are part of the code.</param>
    /// <returns>The rewritten code and any prose around it.</returns>
    public static WholeScopeReply Parse(string reply, string baseCode)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(baseCode);
        var lf = EditText.ToLf(reply);
        var lines = EditText.SplitLines(lf);
        var first = lines.FindIndex(l => l.Trim().Length > 0);
        if (first < 0)
        {
            return new WholeScopeReply(null, string.Empty, "the reply is empty");
        }

        var baseLines = EditText.SplitLines(baseCode);
        var baseFirst = baseLines.FindIndex(l => l.Trim().Length > 0);
        var baseStartsWithFence = baseFirst >= 0 && OpeningFence().IsMatch(baseLines[baseFirst]);
        var baseHasFence = baseLines.Any(l => OpeningFence().IsMatch(l));

        // A fence around the code: the reply starts with one, or prose comes first and the code's own text has no fences.
        var fenced = OpeningFence().IsMatch(lines[first]) ? !baseStartsWithFence : !baseHasFence && lines.Any(l => OpeningFence().IsMatch(l));
        var explanation = string.Empty;
        var codeText = lf;
        if (fenced)
        {
            var (blocks, prose) = FencedCodeParser.Parse(lf);
            var block = blocks.FirstOrDefault(b => b.Code.Trim().Length > 0);
            if (block is null)
            {
                return new WholeScopeReply(null, string.Empty, "the reply has a code fence with no code in it");
            }

            codeText = block.Code;
            explanation = prose;
        }

        var codeLines = TrimBlankEdges([.. EditText.StripLineNumbers(EditText.SplitLines(codeText))]);
        if (codeLines.Count == 0)
        {
            return new WholeScopeReply(null, explanation, "the reply is empty");
        }

        if (codeLines.Any(l => SearchMarker().IsMatch(l)))
        {
            return new WholeScopeReply(null, explanation, "the reply is in edit-block format, not the code itself");
        }

        var code = string.Join('\n', codeLines);
        if (baseCode.EndsWith('\n'))
        {
            code += "\n";
        }

        return new WholeScopeReply(code, explanation, null);
    }

    /// <summary>
    /// Reads a reply that is still streaming for an early preview: the complete lines of the code so far, which later
    /// output can no longer change if the reply keeps its form. An unfinished last line, trailing blank lines and a closing
    /// fence are left out.
    /// </summary>
    /// <param name="partialReply">The reply so far.</param>
    /// <param name="baseCode">The code the reply rewrites (LF).</param>
    /// <param name="stop">Set to <see langword="true"/> when the reply cannot be previewed (it is in edit-block format).</param>
    /// <returns>The code so far without a final line break, or <see langword="null"/> when there is nothing to show yet.</returns>
    public static string? Preview(string partialReply, string baseCode, out bool stop)
    {
        ArgumentNullException.ThrowIfNull(partialReply);
        ArgumentNullException.ThrowIfNull(baseCode);
        stop = false;
        var lines = EditText.SplitLines(EditText.ToLf(partialReply));
        lines.RemoveAt(lines.Count - 1);

        var index = lines.FindIndex(l => l.Trim().Length > 0);
        if (index < 0)
        {
            return null;
        }

        var baseLines = EditText.SplitLines(baseCode);
        var baseFirst = baseLines.FindIndex(l => l.Trim().Length > 0);
        var baseStartsWithFence = baseFirst >= 0 && OpeningFence().IsMatch(baseLines[baseFirst]);

        string? closing = null;
        if (OpeningFence().IsMatch(lines[index]) && !baseStartsWithFence)
        {
            closing = lines[index].Trim();
            closing = closing[..closing.TakeWhile(c => c == closing[0]).Count()];
            index++;
        }

        var code = new List<string>();
        for (; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (closing is not null && trimmed.Length >= closing.Length && trimmed.All(c => c == closing[0]))
            {
                break;
            }

            code.Add(lines[index]);
        }

        if (code.Any(l => SearchMarker().IsMatch(l)))
        {
            stop = true;
            return null;
        }

        var stripped = TrimBlankEdges([.. EditText.StripLineNumbers(code)]);
        return stripped.Count == 0 ? null : string.Join('\n', stripped);
    }

    private static List<string> TrimBlankEdges(List<string> lines)
    {
        var start = 0;
        while (start < lines.Count && lines[start].Trim().Length == 0)
        {
            start++;
        }

        var end = lines.Count;
        while (end > start && lines[end - 1].Trim().Length == 0)
        {
            end--;
        }

        return lines.GetRange(start, end - start);
    }

    [GeneratedRegex(@"^\s*(```+|~~~+)[^`]*$", RegexOptions.CultureInvariant)]
    private static partial Regex OpeningFence();

    [GeneratedRegex(@"^\s*<{5,9}\s*SEARCH\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchMarker();
}
