namespace Pillion.ContextEngine.Chunking.Razor;

/// <summary>The kind of C# block found in a Razor file.</summary>
internal enum RazorBlockKind
{
    /// <summary><c>@code { }</c> or <c>@functions { }</c>: class members.</summary>
    Members,

    /// <summary><c>@{ }</c>: statements.</summary>
    Statements,
}

/// <summary>A C# block in a Razor file. Offsets index the file's text; <see cref="Close"/> is the closing brace.</summary>
internal sealed record RazorBlock(RazorBlockKind Kind, int Start, int Open, int Close);

/// <summary>
/// Finds the C# blocks of a Razor file (<c>@code</c>, <c>@functions</c>, <c>@{ }</c>) with a small lexer that matches
/// braces while skipping C# strings, characters and comments. Everything else (markup, directives, inline
/// expressions, control-flow blocks that mix code and markup) is treated as markup. Linear in the length of the text;
/// an unterminated block runs to the end of the file.
/// </summary>
internal static class RazorScanner
{
    public static List<RazorBlock> Scan(string text)
    {
        var blocks = new List<RazorBlock>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '@')
            {
                i++;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '@')
            {
                i += 2;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*@", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '{')
            {
                var end = MatchBrace(text, i + 1);
                blocks.Add(new RazorBlock(RazorBlockKind.Statements, i, i + 1, end));
                i = end + 1;
                continue;
            }

            var keyword = Keyword(text, i + 1);
            if (keyword is "code" or "functions")
            {
                var open = i + 1 + keyword.Length;
                while (open < text.Length && char.IsWhiteSpace(text[open]))
                {
                    open++;
                }

                if (open < text.Length && text[open] == '{')
                {
                    var end = MatchBrace(text, open);
                    blocks.Add(new RazorBlock(RazorBlockKind.Members, i, open, end));
                    i = end + 1;
                    continue;
                }
            }

            i++;
        }

        return blocks;
    }

    /// <summary>The offset of the brace that closes the one at <paramref name="open"/>, or the last offset when none does.</summary>
    internal static int MatchBrace(string text, int open, int nesting = 0)
    {
        var depth = 0;
        var i = open;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '{':
                    depth++;
                    i++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    i++;
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '/':
                    i = LineEnd(text, i);
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '*':
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? text.Length : close + 2;
                    break;
                case '\'':
                    i = SkipCharacter(text, i);
                    break;
                case '"' or '$' or '@':
                    i = SkipString(text, i, nesting);
                    break;
                default:
                    i++;
                    break;
            }
        }

        return Math.Max(open, text.Length - 1);
    }

    /// <summary>Skips a string literal of any C# form starting at <paramref name="start"/>; returns the next offset.</summary>
    private static int SkipString(string text, int start, int nesting)
    {
        var i = start;
        var interpolated = false;
        var verbatim = false;
        while (i < text.Length && (text[i] == '$' || text[i] == '@'))
        {
            interpolated |= text[i] == '$';
            verbatim |= text[i] == '@';
            i++;
        }

        if (i >= text.Length || text[i] != '"')
        {
            return start + 1;
        }

        var quotes = 0;
        while (i + quotes < text.Length && text[i + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            // Raw string literal: ends at the same number of quotes.
            var terminator = new string('"', quotes);
            var close = text.IndexOf(terminator, i + quotes, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + quotes;
        }

        if (quotes == 2)
        {
            return i + 2; // empty string
        }

        i++;
        while (i < text.Length)
        {
            var c = text[i];
            if (verbatim && c == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (!verbatim && c == '\\')
            {
                i += 2;
                continue;
            }

            if (!verbatim && c == '"')
            {
                return i + 1;
            }

            if (!verbatim && c is '\n' or '\r')
            {
                return i; // unterminated regular string ends at the line break
            }

            if (interpolated && c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }

                // An interpolation hole: code up to its matching brace. Holes nested deeper than a sane program would
                // nest them are skipped to the next closing brace, so hostile input cannot exhaust the stack.
                i = nesting < 16 ? MatchBrace(text, i, nesting + 1) + 1 : NextClosingBrace(text, i) + 1;
                continue;
            }

            i++;
        }

        return text.Length;
    }

    private static int NextClosingBrace(string text, int start)
    {
        var close = text.IndexOf('}', start);
        return close < 0 ? text.Length - 1 : close;
    }

    private static int SkipCharacter(string text, int start)
    {
        var i = start + 1;
        var limit = Math.Min(text.Length, start + 12);
        while (i < limit)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == '\'')
            {
                return i + 1;
            }

            if (text[i] is '\n' or '\r')
            {
                break;
            }

            i++;
        }

        return start + 1;
    }

    private static int LineEnd(string text, int start)
    {
        var end = text.AsSpan(start).IndexOfAny('\n', '\r');
        return end < 0 ? text.Length : start + end;
    }

    private static string Keyword(string text, int start)
    {
        var end = start;
        while (end < text.Length && end - start < 16 && char.IsAsciiLetter(text[end]))
        {
            end++;
        }

        return end < text.Length && char.IsAsciiLetterOrDigit(text[end]) ? string.Empty : text[start..end];
    }
}
