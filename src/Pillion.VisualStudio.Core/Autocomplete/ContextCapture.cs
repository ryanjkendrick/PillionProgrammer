using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>The text around the caret, captured for one completion request.</summary>
public sealed class CapturedContext
{
    /// <summary>Gets or sets the text before the caret (at most <see cref="ContextCapture.MaxPrefixLength"/> characters), including virtual-space indentation.</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Gets or sets the text after the caret (at most <see cref="ContextCapture.MaxSuffixLength"/> characters).</summary>
    public string Suffix { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based caret line.</summary>
    public int CaretLine { get; set; }

    /// <summary>Gets or sets the zero-based caret column, counting virtual spaces.</summary>
    public int CaretColumn { get; set; }

    /// <summary>Gets or sets the caret line's text before the caret, including virtual-space indentation.</summary>
    public string LineBeforeCaret { get; set; } = string.Empty;

    /// <summary>Gets or sets the caret line's text after the caret.</summary>
    public string LineAfterCaret { get; set; } = string.Empty;

    /// <summary>Gets or sets the nearest non-blank line above the caret line, or <see langword="null"/>.</summary>
    public string? PreviousNonBlankLine { get; set; }

    /// <summary>Gets or sets the first lines below the caret line.</summary>
    public IReadOnlyList<string> LinesBelow { get; set; } = [];
}

/// <summary>
/// Captures the prefix and suffix around the caret for a completion request. The prefix is capped at the last 8,000
/// characters and the suffix at the first 2,000 before sending; Pillion Host trims both to its token budget (suffix
/// first). Virtual-space indentation (a caret on an empty, indented line) is added to the prefix so the model sees
/// the indentation, and the suggestion comes back without it.
/// </summary>
public static class ContextCapture
{
    /// <summary>The most characters of prefix sent.</summary>
    public const int MaxPrefixLength = 8000;

    /// <summary>The most characters of suffix sent.</summary>
    public const int MaxSuffixLength = 2000;

    /// <summary>How many lines below the caret line are captured for the stop rules.</summary>
    public const int LinesBelowCount = 5;

    private const int PreviousLineSearch = 50;

    /// <summary>Captures the context.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="caret">The caret position.</param>
    /// <param name="virtualSpaces">The caret's virtual spaces beyond the end of the line (0 when not in virtual space).</param>
    /// <returns>The context.</returns>
    public static CapturedContext Capture(IDocumentText text, int caret, int virtualSpaces = 0)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        if (caret < 0 || caret > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(caret));
        }

        var padding = virtualSpaces > 0 ? new string(' ', virtualSpaces) : string.Empty;
        var prefixStart = Math.Max(0, caret - MaxPrefixLength);
        var suffixLength = Math.Min(MaxSuffixLength, text.Length - caret);

        var line = text.GetLineNumber(caret);
        var lineStart = text.GetLineStart(line);
        var lineText = text.GetLineText(line);
        var column = caret - lineStart;

        string? previous = null;
        for (var i = line - 1; i >= Math.Max(0, line - PreviousLineSearch); i--)
        {
            var candidate = text.GetLineText(i);
            if (candidate.Trim().Length > 0)
            {
                previous = candidate;
                break;
            }
        }

        var below = new List<string>(LinesBelowCount);
        for (var i = line + 1; i < text.LineCount && below.Count < LinesBelowCount; i++)
        {
            below.Add(text.GetLineText(i));
        }

        var prefix = text.GetText(prefixStart, caret - prefixStart) + padding;
        return new CapturedContext
        {
            Prefix = prefix.Length > MaxPrefixLength ? prefix.Substring(prefix.Length - MaxPrefixLength) : prefix,
            Suffix = text.GetText(caret, suffixLength),
            CaretLine = line,
            CaretColumn = column + virtualSpaces,
            LineBeforeCaret = lineText.Substring(0, Math.Min(column, lineText.Length)) + padding,
            LineAfterCaret = column < lineText.Length ? lineText.Substring(column) : string.Empty,
            PreviousNonBlankLine = previous,
            LinesBelow = below,
        };
    }
}
