using System;
using System.Text.RegularExpressions;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>Why a request was made single-line or multi-line (logged, and asserted by the decision-table tests).</summary>
public enum MultiLineReason
{
    /// <summary>Single-line: none of the multi-line rules applied.</summary>
    Default,

    /// <summary>Single-line: autocomplete is degraded because it has been slow.</summary>
    Degraded,

    /// <summary>Single-line: there is code to the right of the caret.</summary>
    MidLine,

    /// <summary>Multi-line: the caret ends a line that opens a block.</summary>
    OpensBlock,

    /// <summary>Multi-line: the caret is on an empty line inside a body.</summary>
    EmptyLineInBody,

    /// <summary>Multi-line: the caret follows a declaration signature.</summary>
    AfterDeclaration,

    /// <summary>Multi-line: the previous suggestion was accepted at the end of a line.</summary>
    AfterAcceptAtLineEnd,

    /// <summary>Multi-line: requested explicitly (the next-suggestion command's multi-line variant).</summary>
    Forced,
}

/// <summary>The inputs of the multi-line decision (design section 5, Completion shaping).</summary>
public sealed class MultiLineInput
{
    /// <summary>Gets or sets the caret line's text before the caret, including any virtual-space indentation.</summary>
    public string LineBeforeCaret { get; set; } = string.Empty;

    /// <summary>Gets or sets the caret line's text after the caret.</summary>
    public string LineAfterCaret { get; set; } = string.Empty;

    /// <summary>Gets or sets the nearest non-blank line above the caret line, or <see langword="null"/>.</summary>
    public string? PreviousNonBlankLine { get; set; }

    /// <summary>Gets or sets the language identifier.</summary>
    public string? LanguageId { get; set; }

    /// <summary>Gets or sets a value indicating whether the previous suggestion was accepted at the end of a line.</summary>
    public bool PreviousAcceptedAtLineEnd { get; set; }

    /// <summary>Gets or sets a value indicating whether autocomplete is degraded (single-line only).</summary>
    public bool Degraded { get; set; }

    /// <summary>Gets or sets a value indicating whether a multi-line suggestion was asked for explicitly.</summary>
    public bool ForceMultiLine { get; set; }
}

/// <summary>
/// Decides whether to ask for a multi-line completion. Single-line is the default; a multi-line request is made when
/// the caret ends a line that opens a block, sits on an empty line inside a body, follows a declaration signature, or
/// when the previous suggestion was accepted at a line end. Degradation forces single-line.
/// </summary>
public static class MultiLineDecision
{
    private static readonly Regex VbBlockOpener = new(
        @"\bThen\s*$|^\s*(?:Do|Else|Try|Finally|Catch\b.*|For\b.*|While\b.*|Select\s+Case\b.*|With\b.*|Using\b.*|SyncLock\b.*)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(50));

    /// <summary>Makes the decision.</summary>
    /// <param name="input">The inputs.</param>
    /// <returns>The reason; multi-line when <see cref="IsMultiLine(MultiLineReason)"/> is true for it.</returns>
    public static MultiLineReason Decide(MultiLineInput input)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (input.Degraded)
        {
            return MultiLineReason.Degraded;
        }

        var after = input.LineAfterCaret.Trim();
        if (after.Length > 0 && after != "}")
        {
            return MultiLineReason.MidLine;
        }

        if (input.ForceMultiLine)
        {
            return MultiLineReason.Forced;
        }

        var before = input.LineBeforeCaret.TrimEnd();
        var style = LanguageFamily.Of(input.LanguageId);
        if (before.Length > 0 && OpensBlock(before, style))
        {
            return MultiLineReason.OpensBlock;
        }

        if (before.Length == 0)
        {
            var previous = input.PreviousNonBlankLine;
            if (previous is not null && DeclarationHeuristic.LooksLikeDeclaration(previous, input.LanguageId))
            {
                return MultiLineReason.AfterDeclaration;
            }

            if (previous is not null && OpensBlock(previous.TrimEnd(), style))
            {
                return MultiLineReason.EmptyLineInBody;
            }

            if (input.LineBeforeCaret.Length > 0 && style != BlockStyle.None)
            {
                return MultiLineReason.EmptyLineInBody; // Indented blank line: inside a body.
            }
        }
        else if (DeclarationHeuristic.LooksLikeDeclaration(before, input.LanguageId) && EndsSignature(before, style))
        {
            return MultiLineReason.AfterDeclaration;
        }

        return input.PreviousAcceptedAtLineEnd ? MultiLineReason.AfterAcceptAtLineEnd : MultiLineReason.Default;
    }

    /// <summary>Gets a value indicating whether a reason means multi-line.</summary>
    /// <param name="reason">The reason.</param>
    /// <returns><see langword="true"/> for the multi-line reasons.</returns>
    public static bool IsMultiLine(MultiLineReason reason) => reason is MultiLineReason.OpensBlock
        or MultiLineReason.EmptyLineInBody
        or MultiLineReason.AfterDeclaration
        or MultiLineReason.AfterAcceptAtLineEnd
        or MultiLineReason.Forced;

    private static bool OpensBlock(string trimmedEnd, BlockStyle style)
    {
        switch (style)
        {
            case BlockStyle.Indentation:
                return trimmedEnd.EndsWith(":", StringComparison.Ordinal) && !trimmedEnd.TrimStart().StartsWith("#", StringComparison.Ordinal);
            case BlockStyle.Keywords:
                try
                {
                    return VbBlockOpener.IsMatch(trimmedEnd);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }

            case BlockStyle.Braces:
                return trimmedEnd.EndsWith("{", StringComparison.Ordinal);
            default:
                return trimmedEnd.EndsWith("{", StringComparison.Ordinal);
        }
    }

    private static bool EndsSignature(string trimmedEnd, BlockStyle style) => style switch
    {
        BlockStyle.Keywords => true,
        BlockStyle.Indentation => trimmedEnd.EndsWith(":", StringComparison.Ordinal),
        _ => trimmedEnd.EndsWith(")", StringComparison.Ordinal) || trimmedEnd.EndsWith("=>", StringComparison.Ordinal)
            || char.IsLetterOrDigit(trimmedEnd[trimmedEnd.Length - 1]) || trimmedEnd.EndsWith(">", StringComparison.Ordinal),
    };
}
