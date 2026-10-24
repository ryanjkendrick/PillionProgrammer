using System.Globalization;
using System.Text.RegularExpressions;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>
/// Finds the citations of a chat reply (design section 7, Hybrid retrieval step 5, work item 2.12): the <c>path:line</c> and
/// <c>path:start-end</c> references in the reply that point into a retrieved chunk the prompt carried.
/// </summary>
/// <remarks>
/// <para>
/// A reference matches a sent chunk when its path is the chunk's path, or a path or file name the chunk's path ends with
/// (compared without case, with either slash), or the chunk's full path; and its lines overlap the chunk's span. Each match
/// is one explicit citation of the lines the model named, clamped to the chunk, with the chunk's id and breadcrumb and
/// <see cref="Citation.ReplyOffset"/> just after the reference. A reference is accepted in backticks, brackets or plain text;
/// a line number written as <c>L12</c> or with a <c>#L</c> anchor is accepted too.
/// </para>
/// <para>
/// When the reply names none of the chunks, every chunk that was sent is returned as an implicit citation: the span of the
/// whole chunk with <see cref="Citation.ReplyOffset"/> -1 (the reply as a whole), so the Chat window can list the sources
/// collapsed. Nothing is returned when no chunk was sent.
/// </para>
/// </remarks>
public static partial class CitationExtractor
{
    /// <summary>The most explicit citations kept for one reply.</summary>
    public const int MaxExplicit = 32;

    /// <summary>Extracts the citations of a reply.</summary>
    /// <param name="reply">The reply text (every chunk's text concatenated).</param>
    /// <param name="sent">The chunks the prompt carried.</param>
    /// <returns>The explicit citations in reply order, else the implicit set, else none.</returns>
    public static Citation[] Extract(string? reply, IReadOnlyList<ContextChunk> sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        if (sent.Count == 0)
        {
            return [];
        }

        var found = Explicit(reply ?? string.Empty, sent);
        return found.Count > 0 ? [.. found] : Implicit(sent);
    }

    /// <summary>Finds the explicit references of a reply into the sent chunks.</summary>
    /// <param name="reply">The reply text.</param>
    /// <param name="sent">The chunks the prompt carried.</param>
    /// <returns>The citations, in reply order, without repeats of the same span.</returns>
    public static IReadOnlyList<Citation> Explicit(string reply, IReadOnlyList<ContextChunk> sent)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(sent);
        var result = new List<Citation>();
        var seen = new HashSet<(string, int, int)>();
        foreach (Match match in Reference().Matches(reply))
        {
            if (!int.TryParse(match.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start <= 0)
            {
                continue;
            }

            var end = match.Groups["end"].Success && int.TryParse(match.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var e) && e >= start ? e : start;
            var path = Normalise(match.Groups["path"].Value);
            var chunk = sent.FirstOrDefault(c => PathMatches(path, c) && start <= c.EndLine && end >= c.StartLine);
            if (chunk is null)
            {
                continue;
            }

            var from = Math.Max(start, chunk.StartLine);
            var to = Math.Min(end, chunk.EndLine);
            if (!seen.Add((chunk.Key, from, to)))
            {
                continue;
            }

            result.Add(new Citation
            {
                FilePath = chunk.FilePath,
                StartLine = from - 1,
                EndLine = to - 1,
                ChunkId = chunk.Key,
                Breadcrumb = chunk.Breadcrumb,
                ReplyOffset = match.Index + match.Length,
            });

            if (result.Count == MaxExplicit)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>The sent chunks as citations of the whole reply.</summary>
    /// <param name="sent">The chunks the prompt carried.</param>
    /// <returns>One citation per chunk, in prompt order, with <see cref="Citation.ReplyOffset"/> -1.</returns>
    public static Citation[] Implicit(IReadOnlyList<ContextChunk> sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        return
        [
            .. sent.Select(c => new Citation
            {
                FilePath = c.FilePath,
                StartLine = Math.Max(0, c.StartLine - 1),
                EndLine = Math.Max(0, c.EndLine - 1),
                ChunkId = c.Key,
                Breadcrumb = c.Breadcrumb,
                ReplyOffset = -1,
            }),
        ];
    }

    private static bool PathMatches(string reference, ContextChunk chunk)
    {
        if (reference.Length == 0)
        {
            return false;
        }

        var path = Normalise(chunk.Path);
        var full = Normalise(chunk.FilePath);
        return string.Equals(reference, path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reference, full, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/" + reference, StringComparison.OrdinalIgnoreCase)
            || full.EndsWith("/" + reference, StringComparison.OrdinalIgnoreCase)
            || reference.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string path)
    {
        var normal = path.Trim().Replace('\\', '/');
        while (normal.StartsWith("./", StringComparison.Ordinal))
        {
            normal = normal[2..];
        }

        return normal;
    }

    // A path with an extension (optionally a drive), then ':' or '#', an optional 'L', a line, and an optional end line after
    // '-', an en dash or '-L'. A 'path:line:column' reference reads as the line alone.
    [GeneratedRegex(@"(?<![\w./\\-])(?<path>(?:[A-Za-z]:[\\/])?[\w.\-/\\]*\w\.\w{1,12})[:#]L?(?<start>\d{1,7})(?:\s?(?:-L|-|–)\s?L?(?<end>\d{1,7}))?(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();
}
