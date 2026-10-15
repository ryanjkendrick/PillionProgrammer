using System.Globalization;
using Pillion.ModelGateway;

namespace Pillion.Host.Context.Mentions;

/// <summary>
/// Cuts text to a token budget keeping its last lines (output panes and build logs, where the end matters most). Like
/// <see cref="TextTruncation"/> it starts from the text's characters-per-token ratio and shrinks until the counted result fits.
/// </summary>
internal static class TailTruncation
{
    private const int MaxRounds = 12;

    /// <summary>Keeps the last lines that fit; the earlier ones are replaced by a marker line.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxTokens">The budget, marker included.</param>
    /// <param name="counter">The token counter.</param>
    /// <returns>The fitted text.</returns>
    public static TruncatedText KeepTail(string text, int maxTokens, IModelTokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(counter);
        var total = counter.Count(text);
        if (total <= maxTokens)
        {
            return new TruncatedText(text, total, false);
        }

        if (maxTokens <= 0)
        {
            return new TruncatedText(string.Empty, 0, true);
        }

        var charsPerToken = text.Length / (double)Math.Max(1, total);
        var targetChars = (int)(maxTokens * charsPerToken * 0.95);
        for (var round = 0; round < MaxRounds && targetChars > 0; round++)
        {
            var start = Math.Max(0, text.Length - targetChars);
            var lineStart = text.IndexOf('\n', start);
            start = lineStart >= 0 && lineStart + 1 < text.Length ? lineStart + 1 : start;
            var omitted = CountLines(text, start);
            var candidate = string.Create(CultureInfo.InvariantCulture, $"[... {omitted} earlier lines omitted ...]\n") + text[start..];
            var tokens = counter.Count(candidate);
            if (tokens <= maxTokens)
            {
                return new TruncatedText(candidate, tokens, true);
            }

            targetChars = (int)(targetChars * Math.Min(0.97, maxTokens / (double)tokens * 0.97));
        }

        return new TruncatedText(string.Empty, 0, true);
    }

    private static int CountLines(string text, int end)
    {
        var count = 0;
        foreach (var c in text.AsSpan(0, end))
        {
            count += c == '\n' ? 1 : 0;
        }

        return count;
    }
}
