using System.Text.RegularExpressions;
using Pillion.ContextEngine.Chunking.Core;

namespace Pillion.ContextEngine.Chunking.Sql;

/// <summary>
/// SQL by statement. TreeSitter.DotNet ships no SQL grammar, so this is a small lexer instead: it skips strings,
/// quoted identifiers, comments and PostgreSQL dollar quotes, tracks <c>BEGIN</c>/<c>CASE</c>...<c>END</c> and
/// parentheses, and ends a statement at a top-level <c>;</c> or a <c>GO</c> batch separator, so a stored procedure
/// stays one unit. Breadcrumb: <c>path &gt; CREATE PROCEDURE dbo.GetOrders</c>. Consecutive <c>INSERT</c>s into the
/// same table are grouped. Statements over the cap split where inner statements start.
/// </summary>
public sealed partial class SqlChunker : SyntaxChunker
{
    /// <inheritdoc />
    public override string Language => LanguageIds.Sql;

    private protected override List<ChunkUnit>? CollectUnits(TokenBudget budget, out string? failure)
    {
        failure = null;
        var document = budget.Document;
        var units = new List<ChunkUnit>();
        foreach (var (start, end) in Statements(document.Text))
        {
            var startLine = document.LineOfOffset(start);
            var endLine = document.LastLineOfSpan(start, end);
            var head = Head(document.Text, start, end);
            var unit = new ChunkUnit(startLine, endLine, ChunkKind.Statement, [], head.Name) { MergeKey = head.MergeKey };
            unit.AddSplitSource(() => InnerStatementStarts(document, startLine, endLine));
            units.Add(unit);
        }

        return units;
    }

    /// <summary>The statements of a script as offset ranges (start inclusive, end exclusive), in order.</summary>
    internal static List<(int Start, int End)> Statements(string text)
    {
        var statements = new List<(int, int)>();
        var start = -1;
        var depth = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Comments are never the start of a statement; they lead into the next one.
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                i = LineEnd(text, i);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                continue;
            }

            if (start < 0 && IsBatchSeparator(text, i, out var afterGo))
            {
                i = afterGo;
                continue;
            }

            if (start < 0)
            {
                start = i;
            }

            switch (c)
            {
                case '\'':
                    i = SkipQuoted(text, i, '\'');
                    continue;
                case '"':
                    i = SkipQuoted(text, i, '"');
                    continue;
                case '`':
                    i = SkipQuoted(text, i, '`');
                    continue;
                case '[':
                    i = SkipQuoted(text, i, ']');
                    continue;
                case '$' when DollarTag(text, i) is { } tag:
                    var closeTag = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                    i = closeTag < 0 ? text.Length : closeTag + tag.Length;
                    continue;
                case '(':
                    depth++;
                    i++;
                    continue;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    i++;
                    continue;
                case ';' when depth == 0:
                    statements.Add((start, i + 1));
                    start = -1;
                    i++;
                    continue;
            }

            if (char.IsAsciiLetter(c) && (i == 0 || !IsWordChar(text[i - 1])))
            {
                var wordEnd = i;
                while (wordEnd < text.Length && IsWordChar(text[wordEnd]))
                {
                    wordEnd++;
                }

                var word = text.AsSpan(i, wordEnd - i);
                if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) && !IsTransactionBegin(text, wordEnd))
                {
                    depth++;
                }
                else if (word.Equals("CASE", StringComparison.OrdinalIgnoreCase))
                {
                    depth++;
                }
                else if (word.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (word.Equals("GO", StringComparison.OrdinalIgnoreCase) && depth == 0
                    && IsBatchSeparator(text, i, out var afterBatch))
                {
                    statements.Add((start, afterBatch));
                    start = -1;
                    i = afterBatch;
                    continue;
                }

                i = wordEnd;
                continue;
            }

            i++;
        }

        if (start >= 0)
        {
            statements.Add((start, text.Length));
        }

        return statements;
    }

    /// <summary>A statement's name for the breadcrumb (its first line, cut at a parenthesis) and its merge key.</summary>
    private static (string Name, string? MergeKey) Head(string text, int start, int end)
    {
        var span = text.AsSpan(start, Math.Min(end - start, 200));
        var stop = span.IndexOfAny('\n', '\r');
        if (stop >= 0)
        {
            span = span[..stop];
        }

        var paren = span.IndexOf('(');
        if (paren > 0)
        {
            span = span[..paren];
        }

        var name = Breadcrumbs.Sanitize(span.TrimEnd().TrimEnd(';').ToString(), 80);
        var insert = InsertInto().Match(name);
        return (name, insert.Success ? "insert|" + insert.Groups[1].Value.ToUpperInvariant() : null);
    }

    /// <summary>Lines inside a statement that begin with a statement keyword: where a long procedure may be split.</summary>
    private static IEnumerable<int> InnerStatementStarts(SourceDocument document, int startLine, int endLine)
    {
        for (var line = startLine + 1; line <= endLine; line++)
        {
            if (StatementKeyword().IsMatch(document.LineSpan(line)))
            {
                yield return line;
            }
        }
    }

    private static bool IsBatchSeparator(string text, int i, out int after)
    {
        after = i;
        var lineStart = i;
        while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t')
        {
            lineStart--;
        }

        if (lineStart > 0 && text[lineStart - 1] is not ('\n' or '\r'))
        {
            return false;
        }

        if (i + 2 > text.Length || !text.AsSpan(i, 2).Equals("GO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lineEnd = LineEnd(text, i);
        var rest = text.AsSpan(i + 2, lineEnd - i - 2).Trim();
        if (!rest.IsEmpty && !int.TryParse(rest, out _))
        {
            return false;
        }

        after = lineEnd;
        return true;
    }

    private static bool IsTransactionBegin(string text, int wordEnd)
    {
        var rest = text.AsSpan(wordEnd, Math.Min(24, text.Length - wordEnd)).TrimStart();
        return rest.StartsWith("TRAN", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("WORK", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("DISTRIBUTED", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith(';')
            || rest.IsEmpty;
    }

    private static string? DollarTag(string text, int i)
    {
        var end = i + 1;
        while (end < text.Length && end - i < 64 && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        return end < text.Length && text[end] == '$' ? text[i..(end + 1)] : null;
    }

    private static int SkipQuoted(string text, int i, char close)
    {
        var j = i + 1;
        while (j < text.Length)
        {
            if (text[j] == close)
            {
                if (j + 1 < text.Length && text[j + 1] == close)
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return text.Length;
    }

    private static int LineEnd(string text, int start)
    {
        var end = text.AsSpan(start).IndexOfAny('\n', '\r');
        return end < 0 ? text.Length : start + end;
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    [GeneratedRegex(@"^INSERT\s+(?:INTO\s+)?([\w\.\[\]""`]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertInto();

    [GeneratedRegex(@"^\s*(?:SELECT|INSERT|UPDATE|DELETE|MERGE|WITH|IF|ELSE|WHILE|DECLARE|SET|BEGIN|RETURN|EXEC|EXECUTE|CREATE|ALTER|DROP|OPEN|FETCH|CLOSE|RAISERROR|THROW|PRINT|FOR|LOOP)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatementKeyword();
}
