using System.Text;
using Pillion.ContextEngine.Chunking;
using Pillion.LocalStore.Index;

namespace Pillion.ContextEngine.Retrieval;

/// <summary>A hit's span after expansion and overlap removal: chunk positions in its file's chunk list and the lines it covers (1-based, inclusive).</summary>
/// <param name="First">The position of the first chunk.</param>
/// <param name="Last">The position of the last chunk.</param>
/// <param name="StartLine">The first line.</param>
/// <param name="EndLine">The last line.</param>
internal readonly record struct PlacedSpan(int First, int Last, int StartLine, int EndLine);

/// <summary>
/// Expansion and overlap removal for hybrid retrieval (design section 7, step 4), over one file's chunks ordered by start
/// line. Pure functions; the retriever loads the chunks.
/// </summary>
internal static class SpanExpander
{
    /// <summary>
    /// Grows a hit to its full member (every part of a split unit, nearest parts first) and then to one neighbouring chunk
    /// on each side, each only while the total token count stays within <paramref name="maxTokens"/>. The hit's own chunk
    /// is always kept.
    /// </summary>
    /// <param name="chunks">The file's chunks, by start line.</param>
    /// <param name="hit">The position of the hit's chunk.</param>
    /// <param name="maxTokens">The token budget of the expanded span.</param>
    /// <returns>The positions of the first and last chunk of the span.</returns>
    public static (int First, int Last) Expand(IReadOnlyList<ChunkRecord> chunks, int hit, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentOutOfRangeException.ThrowIfNegative(hit);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(hit, chunks.Count);

        var first = hit;
        var last = hit;
        var tokens = chunks[hit].TokenCount;

        // 1. The full member: the parts of a unit the chunker split (same breadcrumb segments and part count).
        if (Breadcrumbs.TryParse(chunks[hit].Breadcrumb, out var segments, out _, out var count) && count > 1)
        {
            bool SameUnit(int i) =>
                Breadcrumbs.TryParse(chunks[i].Breadcrumb, out var other, out _, out var otherCount)
                && otherCount == count
                && other.SequenceEqual(segments, StringComparer.Ordinal);

            var leftOpen = true;
            var rightOpen = true;
            while (leftOpen || rightOpen)
            {
                if (rightOpen)
                {
                    rightOpen = last + 1 < chunks.Count && SameUnit(last + 1) && tokens + chunks[last + 1].TokenCount <= maxTokens;
                    if (rightOpen)
                    {
                        last++;
                        tokens += chunks[last].TokenCount;
                    }
                }

                if (leftOpen)
                {
                    leftOpen = first > 0 && SameUnit(first - 1) && tokens + chunks[first - 1].TokenCount <= maxTokens;
                    if (leftOpen)
                    {
                        first--;
                        tokens += chunks[first].TokenCount;
                    }
                }
            }
        }

        // 2. One neighbour on each side, when it fits.
        if (first > 0 && tokens + chunks[first - 1].TokenCount <= maxTokens)
        {
            first--;
            tokens += chunks[first].TokenCount;
        }

        if (last + 1 < chunks.Count && tokens + chunks[last + 1].TokenCount <= maxTokens)
        {
            last++;
        }

        return (first, last);
    }

    /// <summary>
    /// Removes the overlap between an expanded span and the spans already accepted in the same file. The hit is dropped
    /// when every line of its own chunk is already shown; otherwise neighbour chunks that overlap are trimmed from the
    /// ends, and lines still shared (overlapping window chunks) are clipped off.
    /// </summary>
    /// <param name="chunks">The file's chunks, by start line.</param>
    /// <param name="first">The first chunk of the expanded span.</param>
    /// <param name="last">The last chunk of the expanded span.</param>
    /// <param name="hit">The position of the hit's chunk (within the span).</param>
    /// <param name="accepted">The line spans already accepted in this file.</param>
    /// <returns>The span to show, or <see langword="null"/> when the hit adds nothing.</returns>
    public static PlacedSpan? Place(IReadOnlyList<ChunkRecord> chunks, int first, int last, int hit, IReadOnlyList<(int Start, int End)> accepted)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(accepted);
        var own = chunks[hit];
        if (IsCovered(own.StartLine, own.EndLine, accepted))
        {
            return null;
        }

        while (first < hit && Overlaps(chunks[first].StartLine, chunks[first].EndLine, accepted))
        {
            first++;
        }

        while (last > hit && Overlaps(chunks[last].StartLine, chunks[last].EndLine, accepted))
        {
            last--;
        }

        var start = chunks[first].StartLine;
        var end = Math.Max(chunks[last].EndLine, own.EndLine);
        foreach (var (aStart, aEnd) in accepted.OrderBy(a => a.Start))
        {
            if (aStart <= start && aEnd >= start)
            {
                start = aEnd + 1;
            }
        }

        foreach (var (aStart, aEnd) in accepted.OrderByDescending(a => a.End))
        {
            if (aStart <= end && aEnd >= end)
            {
                end = aStart - 1;
            }
        }

        return start > end ? null : new PlacedSpan(first, last, start, end);
    }

    /// <summary>
    /// The text of a span's lines taken from consecutive chunks, each line once even where window chunks overlap.
    /// </summary>
    /// <param name="chunks">The file's chunks, by start line.</param>
    /// <param name="span">The span.</param>
    /// <returns>The lines joined with <c>\n</c>.</returns>
    public static string BuildText(IReadOnlyList<ChunkRecord> chunks, PlacedSpan span)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var builder = new StringBuilder();
        var next = span.StartLine;
        for (var i = span.First; i <= span.Last && next <= span.EndLine; i++)
        {
            var chunk = chunks[i];
            var lines = chunk.Text.Split('\n');
            for (var line = Math.Max(next, chunk.StartLine); line <= Math.Min(chunk.EndLine, span.EndLine); line++)
            {
                var index = line - chunk.StartLine;
                if (index >= lines.Length)
                {
                    break;
                }

                if (builder.Length > 0 || line > span.StartLine)
                {
                    builder.Append('\n');
                }

                builder.Append(lines[index].TrimEnd('\r'));
                next = line + 1;
            }
        }

        return builder.ToString();
    }

    /// <summary>Cuts a chunk's text to a snippet of at most <paramref name="maxChars"/> characters, at a line break when one is near.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxChars">The limit.</param>
    /// <returns>The snippet.</returns>
    public static string Snippet(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = text.LastIndexOf('\n', Math.Max(0, maxChars - 1));
        return (cut > maxChars / 2 ? text[..cut] : text[..maxChars]).TrimEnd() + "\n...";
    }

    private static bool Overlaps(int start, int end, IReadOnlyList<(int Start, int End)> accepted) =>
        accepted.Any(a => a.Start <= end && a.End >= start);

    private static bool IsCovered(int start, int end, IReadOnlyList<(int Start, int End)> accepted)
    {
        var next = start;
        foreach (var (aStart, aEnd) in accepted.OrderBy(a => a.Start))
        {
            if (aStart > next)
            {
                break;
            }

            next = Math.Max(next, aEnd + 1);
            if (next > end)
            {
                return true;
            }
        }

        return next > end;
    }
}
