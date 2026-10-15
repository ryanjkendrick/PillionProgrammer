using System.Buffers;
using System.Text.RegularExpressions;

namespace Pillion.ContextEngine.Chunking.Core;

/// <summary>
/// Turns units into chunks. <see cref="AssemblePartitioned"/> makes the units cover every line exactly once (overlaps
/// merged, runs with the same merge key merged, gap lines given to a neighbour) and then fits each into the token cap;
/// <see cref="Fit"/> fits one unit, splitting it into parts at split points (statement boundaries) or line boundaries,
/// and truncating a single line that is over the cap on its own. All work is linear in the size of the text apart from
/// a logarithmic number of re-counts for the rare chunk whose estimate was wrong.
/// </summary>
internal static partial class ChunkAssembler
{
    private static readonly SearchValues<char> s_closers = SearchValues.Create("}])>;,");

    /// <summary>Partitions the document's lines among the units and fits each one.</summary>
    public static List<Chunk> AssemblePartitioned(TokenBudget budget, IEnumerable<ChunkUnit> units, string language)
    {
        var document = budget.Document;
        var lineCount = document.LineCount;
        var chunks = new List<Chunk>();
        if (document.IsBlank)
        {
            return chunks;
        }

        var ordered = units
            .Where(u => u.EndLine >= 1 && u.StartLine <= lineCount)
            .Select(u =>
            {
                u.StartLine = Math.Max(1, u.StartLine);
                u.EndLine = Math.Clamp(u.EndLine, u.StartLine, lineCount);
                return u;
            })
            .OrderBy(u => u.StartLine)
            .ThenByDescending(u => u.EndLine)
            .ToList();

        var merged = new List<ChunkUnit>(ordered.Count);
        foreach (var unit in ordered)
        {
            if (merged.Count > 0)
            {
                var previous = merged[^1];
                if (unit.StartLine <= previous.EndLine)
                {
                    previous.Absorb(unit, keepNames: unit.MergeKey is not null && unit.MergeKey == previous.MergeKey);
                    continue;
                }

                if (unit.MergeKey is not null && unit.MergeKey == previous.MergeKey)
                {
                    previous.Absorb(unit, keepNames: true);
                    continue;
                }
            }

            merged.Add(unit);
        }

        if (merged.Count == 0)
        {
            merged.Add(new ChunkUnit(1, lineCount, ChunkKind.Document, []));
        }

        // Gap lines: blank lines and closing lines right after a unit stay with it; the rest (comments, doc comments,
        // attributes the parser left out, preprocessor lines) lead into the next unit.
        merged[0].StartLine = 1;
        for (var i = 1; i < merged.Count; i++)
        {
            var previous = merged[i - 1];
            var current = merged[i];
            var line = previous.EndLine + 1;
            while (line < current.StartLine && IsTrailingLine(document.LineSpan(line)))
            {
                line++;
            }

            previous.EndLine = line - 1;
            if (line < current.StartLine)
            {
                current.AddSplit(current.StartLine);
                current.StartLine = line;
            }
        }

        merged[^1].EndLine = lineCount;

        foreach (var unit in merged)
        {
            Fit(budget, unit, language, chunks);
        }

        return chunks;
    }

    /// <summary>Fits one unit into the token cap and appends its chunk or parts.</summary>
    public static void Fit(TokenBudget budget, ChunkUnit unit, string language, List<Chunk> output)
    {
        var document = budget.Document;
        var options = budget.Options;
        var cap = options.MaxTokens;
        var start = unit.StartLine;
        var end = unit.EndLine;

        var breadcrumbBudget = Math.Max(1, Math.Min(options.MaxBreadcrumbTokens, cap / 4));
        var breadcrumb = Breadcrumbs.Fit(document.Path, unit.Segments(), breadcrumbBudget, budget.CountContent);
        var breadcrumbTokens = budget.CountContent(breadcrumb);

        if (budget.Overhead + breadcrumbTokens + budget.RangeTokens(start, end) <= cap)
        {
            var text = document.Slice(start, end);
            var count = budget.Count(breadcrumb + "\n" + text);
            if (count <= cap)
            {
                output.Add(NewChunk(document, start, end, unit.Kind, breadcrumb, text, count, language, 1, 1, false));
                return;
            }
        }

        // Several parts. Reserve room for the widest marker this unit could need, then pack lines.
        var lines = end - start + 1;
        var reserve = Breadcrumbs.WithPart(breadcrumb, lines, lines);
        var lineBudget = Math.Max(1, cap - budget.Overhead - budget.CountContent(reserve));
        var pieces = new List<Piece>();
        foreach (var (pieceStart, pieceEnd) in Pack(budget, unit, start, end, lineBudget))
        {
            Verify(budget, reserve, pieceStart, pieceEnd, pieces);
        }

        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            var label = pieces.Count == 1 ? breadcrumb : Breadcrumbs.WithPart(breadcrumb, i + 1, pieces.Count);
            var text = piece.Text;
            var truncated = piece.Truncated;
            var count = budget.Count(label + "\n" + text);
            if (count > cap)
            {
                text = Truncate(budget, label, text);
                truncated = true;
                count = budget.Count(label + "\n" + text);
            }

            output.Add(NewChunk(document, piece.Start, piece.End, unit.Kind, label, text, count, language, i + 1,
                pieces.Count, truncated));
        }
    }

    /// <summary>
    /// Packs lines greedily up to the budget. When the next line does not fit, the part ends before the last split
    /// point that leaves the part at least a third full, or else at the last line that fit.
    /// </summary>
    private static List<(int Start, int End)> Pack(TokenBudget budget, ChunkUnit unit, int start, int end, int lineBudget)
    {
        var ranges = new List<(int, int)>();
        int[]? splits = null;
        var part = start;
        while (part <= end)
        {
            long used = 0;
            var next = part;
            while (next <= end && used + budget.LineTokens(next) <= lineBudget)
            {
                used += budget.LineTokens(next);
                next++;
            }

            if (next > end)
            {
                ranges.Add((part, end));
                break;
            }

            if (next == part)
            {
                ranges.Add((part, part));
                part++;
                continue;
            }

            splits ??= unit.ResolveSplits(start, end);
            var cut = next;
            var index = Array.BinarySearch(splits, next);
            index = index >= 0 ? index : ~index - 1;
            if (index >= 0 && splits[index] > part && budget.RangeTokens(part, splits[index] - 1) * 3 >= lineBudget)
            {
                cut = splits[index];
            }

            ranges.Add((part, cut - 1));
            part = cut;
        }

        return ranges;
    }

    /// <summary>Checks a packed range with a real count; halves it (or truncates a single line) until it fits.</summary>
    private static void Verify(TokenBudget budget, string reserve, int start, int end, List<Piece> pieces)
    {
        var document = budget.Document;
        var text = document.Slice(start, end);
        if (budget.Count(reserve + "\n" + text) <= budget.MaxTokens)
        {
            pieces.Add(new Piece(start, end, text, false));
            return;
        }

        if (start == end)
        {
            pieces.Add(new Piece(start, end, Truncate(budget, reserve, text), true));
            return;
        }

        var half = budget.RangeTokens(start, end) / 2;
        var middle = start;
        while (middle < end - 1 && budget.RangeTokens(start, middle) < half)
        {
            middle++;
        }

        Verify(budget, reserve, start, middle, pieces);
        Verify(budget, reserve, middle + 1, end, pieces);
    }

    /// <summary>The longest prefix of <paramref name="text"/> that fits under the cap after the breadcrumb.</summary>
    private static string Truncate(TokenBudget budget, string breadcrumb, string text)
    {
        var cap = budget.MaxTokens;

        // A token is at least one character and WordPiece maps words over 100 characters to one token, so a prefix of
        // cap * 128 characters is always enough to search.
        var high = (int)Math.Min(text.Length, (long)cap * 128);
        var length = breadcrumb.Length + 1 + high;
        var buffer = ArrayPool<char>.Shared.Rent(length);
        try
        {
            breadcrumb.CopyTo(buffer);
            buffer[breadcrumb.Length] = '\n';
            text.AsSpan(0, high).CopyTo(buffer.AsSpan(breadcrumb.Length + 1));
            var low = 0;
            while (low < high)
            {
                var middle = low + ((high - low + 1) / 2);
                if (budget.Count(buffer.AsSpan(0, breadcrumb.Length + 1 + middle)) <= cap)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (low > 0 && char.IsHighSurrogate(text[low - 1]))
            {
                low--;
            }

            return text[..low];
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static Chunk NewChunk(SourceDocument document, int start, int end, ChunkKind kind, string breadcrumb,
        string text, int count, string language, int part, int partCount, bool truncated) => new()
        {
            FilePath = document.Path,
            StartLine = start,
            EndLine = end,
            Kind = kind,
            Breadcrumb = breadcrumb,
            Text = text,
            TokenCount = count,
            Language = language,
            Part = part,
            PartCount = partCount,
            IsTruncated = truncated,
        };

    /// <summary>
    /// A line that belongs with the unit above it: blank, only closing punctuation, a closing tag, <c>End ...</c>
    /// (VB, SQL), <c>#endregion</c>, <c>#endif</c> or a SQL <c>GO</c>.
    /// </summary>
    internal static bool IsTrailingLine(ReadOnlySpan<char> line)
    {
        var trimmed = line.Trim();
        if (trimmed.IsEmpty)
        {
            return true;
        }

        if (!trimmed.ContainsAnyExcept(s_closers))
        {
            return true;
        }

        return trimmed.Length <= 80 && TrailingLine().IsMatch(trimmed);
    }

    [GeneratedRegex(@"^(?:</[A-Za-z][\w:.-]*>|end(?:\s+\w+)?\s*;?|#\s*end\s*region\b.*|#\s*endif\b.*|go(?:\s+\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLine();

    private sealed record Piece(int Start, int End, string Text, bool Truncated);
}
