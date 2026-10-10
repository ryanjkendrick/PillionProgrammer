using System;

namespace Pillion.VisualStudio.Core.Signals;

/// <summary>Shortens Output window text to its end, where the build result and the last errors are.</summary>
public static class OutputTail
{
    /// <summary>The line that starts a truncated text, so the reader knows it begins mid-output.</summary>
    public const string Marker = "[earlier output omitted]";

    /// <summary>
    /// Keeps the last <paramref name="maxChars"/> characters of <paramref name="text"/>, starting at a line boundary when
    /// there is one near the cut, and puts <see cref="Marker"/> on its own line in front when anything was dropped. The
    /// result, marker included, is never longer than <paramref name="maxChars"/>.
    /// </summary>
    /// <param name="text">The full text.</param>
    /// <param name="maxChars">The most characters to return; at least 1.</param>
    /// <returns>The text, or its tail.</returns>
    public static string Truncate(string text, int maxChars)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (maxChars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "The limit must be at least 1.");
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var prefix = Marker + "\n";
        if (maxChars <= prefix.Length + 16)
        {
            // Too small for a marker to be worth it: the plain tail.
            return text.Substring(text.Length - maxChars);
        }

        var keep = maxChars - prefix.Length;
        var start = text.Length - keep;

        // Begin at the next line start when one is within the first quarter of the kept text, so the first line is whole.
        var lineBreak = text.IndexOf('\n', start);
        if (lineBreak >= 0 && lineBreak - start <= keep / 4 && lineBreak + 1 < text.Length)
        {
            start = lineBreak + 1;
        }

        return prefix + text.Substring(start);
    }
}
