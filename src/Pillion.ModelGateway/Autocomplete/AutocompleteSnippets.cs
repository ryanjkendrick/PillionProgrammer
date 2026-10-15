using System.Security.Cryptography;
using System.Text;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>A retrieved span of code the autocomplete prompt shows before the file prefix (design section 7, Autocomplete prompt budget).</summary>
/// <param name="Path">The path shown in the prompt (relative to the repository root; the prompt shows the file name only).</param>
/// <param name="StartLine">The 1-based first line of the span.</param>
/// <param name="EndLine">The 1-based last line of the span (inclusive).</param>
/// <param name="Text">The text of the span.</param>
public sealed record AutocompleteSnippet(string Path, int StartLine, int EndLine, string Text);

/// <summary>
/// Supplies the retrieved snippets of an autocomplete request (work item 2.12): at most <see cref="AutocompleteSnippets.MaxSnippets"/>,
/// chosen once per enclosing member and returned unchanged for every keystroke in it, so the prompt prefix the local model sees
/// does not change and its prompt cache keeps hitting. Pillion Host implements it over hybrid retrieval; without one the
/// prompt carries no snippets.
/// </summary>
public interface IAutocompleteSnippetSource
{
    /// <summary>Gets the snippets for a request; empty when there are none (no index, retrieval off, or it ran out of time).</summary>
    /// <param name="request">The completion request.</param>
    /// <param name="cancellationToken">A token that cancels the call (the next keystroke).</param>
    /// <returns>The snippets, best first.</returns>
    ValueTask<IReadOnlyList<AutocompleteSnippet>> GetSnippetsAsync(CompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>Renders autocomplete snippets as terse comment lines in the language's syntax and fits them into a token allowance.</summary>
public static class AutocompleteSnippets
{
    /// <summary>The most snippets one prompt shows.</summary>
    public const int MaxSnippets = 3;

    /// <summary>The most lines of one snippet shown.</summary>
    public const int MaxLines = 12;

    /// <summary>The longest snippet line kept, in characters.</summary>
    public const int MaxLineLength = 160;

    /// <summary>
    /// Renders the snippets: per snippet a <c>from: Name.cs:12-20</c> line, then its lines, every line a comment in the
    /// language's syntax with the common indentation removed; at most <see cref="MaxSnippets"/> snippets and
    /// <see cref="MaxLines"/> lines each. Whole snippets are dropped from the end, then lines from the end of the last
    /// one, until the result fits <paramref name="allowance"/>. The same snippets and allowance always give the same text.
    /// </summary>
    /// <param name="snippets">The snippets, best first.</param>
    /// <param name="comment">The comment markers.</param>
    /// <param name="allowance">The most tokens the block may take.</param>
    /// <param name="counter">The model's token counter.</param>
    /// <returns>The block (ending with a newline, or empty), its tokens and the number of snippets in it.</returns>
    public static (string Text, int Tokens, int Count) Render(IReadOnlyList<AutocompleteSnippet> snippets, (string Open, string Close) comment, int allowance, IModelTokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(snippets);
        ArgumentNullException.ThrowIfNull(counter);
        if (allowance <= 0 || snippets.Count == 0)
        {
            return (string.Empty, 0, 0);
        }

        var rendered = snippets
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Text))
            .Take(MaxSnippets)
            .Select(s => Lines(s, comment))
            .ToList();

        for (var count = rendered.Count; count > 0; count--)
        {
            var text = string.Concat(rendered.Take(count).SelectMany(l => l));
            var tokens = counter.Count(text);
            if (tokens <= allowance)
            {
                return (text, tokens, count);
            }

            if (count == 1)
            {
                // The best snippet alone is too long: keep its first lines (the heading line and at least one more).
                var lines = rendered[0];
                for (var keep = lines.Count - 1; keep >= 2; keep--)
                {
                    var cut = string.Concat(lines.Take(keep));
                    var cutTokens = counter.Count(cut);
                    if (cutTokens <= allowance)
                    {
                        return (cut, cutTokens, 1);
                    }
                }
            }
        }

        return (string.Empty, 0, 0);
    }

    /// <summary>A short fingerprint of a snippet set (the autocomplete cache keeps completions per retrieved context).</summary>
    /// <param name="snippets">The snippets.</param>
    /// <returns>Empty for no snippets; otherwise 16 hex digits.</returns>
    public static string Fingerprint(IReadOnlyList<AutocompleteSnippet> snippets)
    {
        ArgumentNullException.ThrowIfNull(snippets);
        if (snippets.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var snippet in snippets.Take(MaxSnippets))
        {
            builder.Append(snippet.Path).Append('\0').Append(snippet.StartLine).Append('\0').Append(snippet.EndLine).Append('\0').Append(snippet.Text).Append('\u0001');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())), 0, 8);
    }

    private static List<string> Lines(AutocompleteSnippet snippet, (string Open, string Close) comment)
    {
        string Line(string text) => comment.Open + Escape(text, comment.Close) + comment.Close + "\n";

        var name = System.IO.Path.GetFileName(snippet.Path.Replace('\\', '/').TrimEnd('/'));
        var span = snippet.EndLine > snippet.StartLine
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}:{snippet.StartLine}-{snippet.EndLine}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}:{snippet.StartLine}");
        var result = new List<string> { Line("from: " + span) };

        var lines = snippet.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        lines = [.. lines.Where(l => l.Length > 0).Take(MaxLines)];
        var indent = lines.Count == 0 ? 0 : lines.Min(l => l.Length - l.TrimStart().Length);
        foreach (var line in lines)
        {
            var text = line[Math.Min(indent, line.Length)..].Replace('\t', ' ');
            result.Add(Line(text.Length > MaxLineLength ? text[..MaxLineLength] : text));
        }

        return result;
    }

    /// <summary>Keeps a line from closing its comment early (a <c>*/</c> in a CSS snippet, a <c>--&gt;</c> in markup).</summary>
    private static string Escape(string text, string close) =>
        close.Trim().Length == 0 ? text : text.Replace(close.Trim(), close.Trim()[..1] + " " + close.Trim()[1..], StringComparison.Ordinal);
}
