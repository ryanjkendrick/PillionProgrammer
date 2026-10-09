using System.Globalization;
using System.Text;

namespace Pillion.ModelGateway;

/// <summary>The outcome of fitting a text into a token budget.</summary>
/// <param name="Text">The text, possibly shortened, with an elision marker where it was cut.</param>
/// <param name="Tokens">The token count of <paramref name="Text"/>.</param>
/// <param name="Truncated">Whether any of the original text was removed.</param>
public readonly record struct TruncatedText(string Text, int Tokens, bool Truncated);

/// <summary>
/// Cuts text to a token budget on line boundaries (design section 7, Context assembly). Counting every candidate prefix
/// with a real tokenizer would be slow for large files, so a cut starts from the text's own characters-per-token ratio and
/// is verified with the counter, shrinking a few percent at a time until it fits (usually one or two counts).
/// </summary>
public static class TextTruncation
{
    private const int MaxRounds = 12;

    /// <summary>Keeps the first lines that fit; the rest is replaced by an elision marker.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxTokens">The budget, marker included.</param>
    /// <param name="counter">The token counter.</param>
    /// <returns>The fitted text.</returns>
    public static TruncatedText KeepHead(string text, int maxTokens, IModelTokenCounter counter) =>
        Fit(text, maxTokens, counter, TruncationMode.Head);

    /// <summary>Keeps the first and the last lines, about 60% and 40% of the budget, with an elision marker between them.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxTokens">The budget, marker included.</param>
    /// <param name="counter">The token counter.</param>
    /// <returns>The fitted text.</returns>
    public static TruncatedText KeepHeadAndTail(string text, int maxTokens, IModelTokenCounter counter) =>
        Fit(text, maxTokens, counter, TruncationMode.HeadAndTail);

    /// <summary>
    /// Keeps the lines around the middle and cuts both ends, for a region centred on the caret. A marker at each cut end
    /// says that lines above or below were left out.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="maxTokens">The budget, markers included.</param>
    /// <param name="counter">The token counter.</param>
    /// <returns>The fitted text.</returns>
    public static TruncatedText KeepMiddle(string text, int maxTokens, IModelTokenCounter counter) =>
        Fit(text, maxTokens, counter, TruncationMode.Middle);

    /// <summary>Fits a text; <paramref name="knownTotal"/> is its token count when the caller has already counted it.</summary>
    internal static TruncatedText Fit(string text, int maxTokens, IModelTokenCounter counter, TruncationMode mode, int knownTotal = -1)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(counter);
        var total = knownTotal >= 0 ? knownTotal : counter.Count(text);
        if (total <= maxTokens)
        {
            return new TruncatedText(text, total, false);
        }

        if (maxTokens <= 0)
        {
            return new TruncatedText(string.Empty, 0, true);
        }

        var lines = SplitLines(text);
        var charsPerToken = text.Length / (double)Math.Max(1, total);

        // Start from the ratio, aiming a little under the budget; shrink until the counted result fits.
        var targetChars = (int)(maxTokens * charsPerToken * 0.95);
        for (var round = 0; round < MaxRounds && targetChars > 0; round++)
        {
            var candidate = Build(text, lines, targetChars, mode);
            var tokens = counter.Count(candidate);
            if (tokens <= maxTokens)
            {
                return new TruncatedText(candidate, tokens, true);
            }

            // Shrink in proportion to the overshoot, and by at least 3%, so the loop always ends.
            var factor = Math.Min(0.97, maxTokens / (double)tokens * 0.97);
            targetChars = (int)(targetChars * factor);
        }

        return new TruncatedText(string.Empty, 0, true);
    }

    private static string Build(string text, List<(int Start, int Length)> lines, int targetChars, TruncationMode mode)
    {
        switch (mode)
        {
            case TruncationMode.Head:
            {
                var headEnd = HeadEnd(lines, targetChars, out var kept);
                return text[..headEnd].TrimEnd('\r', '\n') + "\n" + Marker(lines.Count - kept);
            }

            case TruncationMode.HeadAndTail:
            {
                var headChars = (int)(targetChars * 0.6);
                var headEnd = HeadEnd(lines, headChars, out var headLines);
                var tailStart = TailStart(lines, targetChars - headChars, text.Length, headLines, out var tailLines);
                var omitted = lines.Count - headLines - tailLines;
                return omitted <= 0
                    ? text
                    : text[..headEnd].TrimEnd('\r', '\n') + "\n" + Marker(omitted) + "\n" + text[tailStart..].TrimStart('\r', '\n');
            }

            default:
            {
                // Walk out from the middle line, half the characters each way.
                var half = targetChars / 2;
                var middle = lines.Count / 2;
                var startLine = middle;
                var chars = 0;
                while (startLine > 0 && chars + lines[startLine - 1].Length <= half)
                {
                    startLine--;
                    chars += lines[startLine].Length;
                }

                var endLine = middle;
                chars = 0;
                while (endLine < lines.Count - 1 && chars + lines[endLine].Length <= half)
                {
                    chars += lines[endLine].Length;
                    endLine++;
                }

                var from = lines[startLine].Start;
                var to = lines[endLine].Start + lines[endLine].Length;
                var builder = new StringBuilder();
                if (startLine > 0)
                {
                    builder.Append(Marker(startLine)).Append('\n');
                }

                builder.Append(text.AsSpan(from, to - from).TrimEnd("\r\n"));
                if (endLine < lines.Count - 1)
                {
                    builder.Append('\n').Append(Marker(lines.Count - 1 - endLine));
                }

                return builder.ToString();
            }
        }
    }

    private static string Marker(int omitted) =>
        omitted > 0 ? string.Create(CultureInfo.InvariantCulture, $"[... {omitted} lines omitted ...]") : "[... truncated ...]";

    /// <summary>Gets the end index of the longest run of whole lines of at most <paramref name="chars"/> characters (cut inside the first line if it alone is longer).</summary>
    private static int HeadEnd(List<(int Start, int Length)> lines, int chars, out int kept)
    {
        var used = 0;
        kept = 0;
        foreach (var (_, length) in lines)
        {
            if (used + length > chars)
            {
                break;
            }

            used += length;
            kept++;
        }

        if (kept == 0)
        {
            kept = 1;
            return Math.Min(chars, lines[0].Length);
        }

        return used;
    }

    private static int TailStart(List<(int Start, int Length)> lines, int chars, int textLength, int headLines, out int kept)
    {
        var used = 0;
        kept = 0;
        for (var i = lines.Count - 1; i >= headLines; i--)
        {
            if (used + lines[i].Length > chars)
            {
                break;
            }

            used += lines[i].Length;
            kept++;
        }

        return textLength - used;
    }

    private static List<(int Start, int Length)> SplitLines(string text)
    {
        var lines = new List<(int, int)>();
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            lines.Add((start, end - start));
            start = end;
        }

        if (lines.Count == 0)
        {
            lines.Add((0, 0));
        }

        return lines;
    }

    internal enum TruncationMode
    {
        Head,
        HeadAndTail,
        Middle,
    }
}
