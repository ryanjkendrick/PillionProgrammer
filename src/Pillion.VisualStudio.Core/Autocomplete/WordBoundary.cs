using System;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>Splits a suggestion for word-at-a-time accept (Ctrl+Right).</summary>
public static class WordBoundary
{
    /// <summary>
    /// Gets the length of the next word of a suggestion: leading whitespace (not crossing a line break unless the
    /// suggestion starts with one), then a run of identifier characters, or else one run of the same punctuation.
    /// A line break at the start is taken together with the next line's indentation and first word.
    /// </summary>
    /// <param name="suggestion">The text still suggested after the caret.</param>
    /// <returns>The number of characters to accept (at least 1 for a non-empty suggestion).</returns>
    public static int NextWordLength(string suggestion)
    {
        if (string.IsNullOrEmpty(suggestion))
        {
            return 0;
        }

        var i = 0;

        // A line break, then the next line's indentation.
        if (suggestion[i] is '\r' or '\n')
        {
            if (suggestion[i] == '\r' && i + 1 < suggestion.Length && suggestion[i + 1] == '\n')
            {
                i++;
            }

            i++;
        }

        while (i < suggestion.Length && suggestion[i] is ' ' or '\t')
        {
            i++;
        }

        if (i >= suggestion.Length || suggestion[i] is '\r' or '\n')
        {
            return Math.Max(1, i);
        }

        if (IsWordChar(suggestion[i]))
        {
            while (i < suggestion.Length && IsWordChar(suggestion[i]))
            {
                i++;
            }
        }
        else
        {
            var punctuation = suggestion[i];
            while (i < suggestion.Length && suggestion[i] == punctuation)
            {
                i++;
            }
        }

        return i;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
