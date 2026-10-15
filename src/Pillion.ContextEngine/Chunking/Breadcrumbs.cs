using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pillion.ContextEngine.Chunking;

/// <summary>
/// The breadcrumb format: <c>path &gt; namespace &gt; type &gt; member</c>, segments joined by <c>" &gt; "</c>, with an
/// optional <c>" (part n of m)"</c> suffix on the parts of a split unit (design section 7). A breadcrumb is one line;
/// no segment is empty, starts or ends with white space, or contains <c>" &gt;"</c>, so splitting on the separator
/// always recovers the segments. The first segment is the path, the file name alone when the path would not fit the
/// breadcrumb budget, or <c>…</c> as a last resort.
/// </summary>
public static partial class Breadcrumbs
{
    /// <summary>The separator between segments.</summary>
    public const string Separator = " > ";

    /// <summary>The longest a segment other than the path may be before it is shortened with an ellipsis.</summary>
    public const int MaxSegmentLength = 120;

    private const string Ellipsis = "\u2026";

    /// <summary>Formats a breadcrumb from a path and the names under it.</summary>
    /// <param name="path">The file path (first segment).</param>
    /// <param name="segments">Namespace, type and member names, outermost first; empty ones are skipped.</param>
    /// <returns>The breadcrumb.</returns>
    public static string Format(string path, IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var first = Sanitize(path, int.MaxValue);
        var builder = new StringBuilder(first.Length > 0 ? first : Ellipsis);
        foreach (var segment in segments)
        {
            var clean = Sanitize(segment, MaxSegmentLength);
            if (clean.Length > 0)
            {
                builder.Append(Separator).Append(clean);
            }
        }

        return builder.ToString();
    }

    /// <summary>Adds the part marker to a breadcrumb.</summary>
    /// <param name="breadcrumb">The breadcrumb without a marker.</param>
    /// <param name="part">The part number, from 1.</param>
    /// <param name="count">The number of parts.</param>
    /// <returns><paramref name="breadcrumb"/> followed by <c>" (part n of m)"</c>.</returns>
    public static string WithPart(string breadcrumb, int part, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{breadcrumb} (part {part} of {count})");

    /// <summary>Splits a breadcrumb into its segments and part marker.</summary>
    /// <param name="breadcrumb">The breadcrumb.</param>
    /// <param name="segments">The segments, path first.</param>
    /// <param name="part">The part number, or 1 without a marker.</param>
    /// <param name="count">The part count, or 1 without a marker.</param>
    /// <returns>True when the breadcrumb is well formed.</returns>
    public static bool TryParse(string? breadcrumb, out IReadOnlyList<string> segments, out int part, out int count)
    {
        segments = [];
        part = 1;
        count = 1;
        if (string.IsNullOrEmpty(breadcrumb) || breadcrumb.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            return false;
        }

        var body = breadcrumb;
        var marker = PartMarker().Match(breadcrumb);
        if (marker.Success)
        {
            if (!int.TryParse(marker.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out part)
                || !int.TryParse(marker.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out count)
                || part < 1 || part > count)
            {
                return false;
            }

            body = breadcrumb[..marker.Index];
        }

        var parts = body.Split(Separator);
        foreach (var segment in parts)
        {
            if (segment.Length == 0 || char.IsWhiteSpace(segment[0]) || char.IsWhiteSpace(segment[^1])
                || segment.Contains(" >", StringComparison.Ordinal))
            {
                return false;
            }
        }

        segments = parts;
        return true;
    }

    /// <summary>Checks a breadcrumb against the format.</summary>
    /// <param name="breadcrumb">The breadcrumb.</param>
    /// <returns>True when it is well formed.</returns>
    public static bool IsWellFormed(string? breadcrumb) => TryParse(breadcrumb, out _, out _, out _);

    /// <summary>
    /// Makes a name safe to use as a segment: white space runs (including line breaks) become one space, the ends are
    /// trimmed, <c>" &gt;"</c> loses its space so it cannot be read as a separator, and long names are cut with an
    /// ellipsis.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="maxLength">The longest result before it is cut.</param>
    /// <returns>The segment, or an empty string when nothing printable is left.</returns>
    public static string Sanitize(string? name, int maxLength = MaxSegmentLength)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        maxLength = Math.Max(maxLength, 1);
        var builder = new StringBuilder(Math.Min(name.Length, maxLength) + 1);
        var pendingSpace = false;
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace && c != '>')
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(c);
            if (builder.Length > maxLength)
            {
                break;
            }
        }

        if (builder.Length > maxLength)
        {
            var keep = Math.Max(1, maxLength - 1);
            if (char.IsHighSurrogate(builder[keep - 1]))
            {
                keep--;
            }

            builder.Length = keep;
            builder.Append('…');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Fits a breadcrumb into a token budget, shortening it step by step: the full path; the file name instead of the
    /// path; every segment cut to 32 characters; the file name and the innermost segment; the file name alone; an
    /// ellipsis.
    /// </summary>
    internal static string Fit(string path, IReadOnlyList<string> segments, int budget, Func<string, int> tokens)
    {
        var full = Format(path, segments);
        if (tokens(full) <= budget)
        {
            return full;
        }

        var fileName = FileName(path);
        var candidates = new List<string>
        {
            Format(fileName, segments),
            Format(fileName, segments.Select(s => Sanitize(s, 32))),
        };
        if (segments.Count > 0)
        {
            candidates.Add(Format(fileName, [Sanitize(segments[^1], 32)]));
        }

        candidates.Add(Sanitize(fileName, 32));
        foreach (var candidate in candidates)
        {
            if (candidate.Length > 0 && tokens(candidate) <= budget)
            {
                return candidate;
            }
        }

        return Ellipsis;
    }

    private static string FileName(string path)
    {
        var cut = path.AsSpan().LastIndexOfAny('/', '\\');
        var name = cut >= 0 ? path[(cut + 1)..] : path;
        return Sanitize(name, int.MaxValue) is { Length: > 0 } clean ? clean : Ellipsis;
    }

    [GeneratedRegex(@" \(part (\d+) of (\d+)\)\z", RegexOptions.CultureInvariant)]
    private static partial Regex PartMarker();
}
