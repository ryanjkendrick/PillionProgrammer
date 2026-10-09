using System;
using System.Text.RegularExpressions;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Finds the declaration that encloses the caret without a language service: the nearest preceding line that looks
/// like a declaration (a function, method, property or type header) and is indented less than the code around the
/// caret. Used for languages without Roslyn and whenever the Roslyn lookup is unavailable or too slow.
/// </summary>
public static class DeclarationHeuristic
{
    /// <summary>The most lines scanned above the caret.</summary>
    public const int MaxLinesScanned = 400;

    /// <summary>The longest declaration text returned.</summary>
    public const int MaxLength = 300;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private const string CModifiers =
        @"(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|unsafe|new|partial|readonly|export|default|declare|const|inline|constexpr|final|synchronized|file|required|fn|pub)\s+)";

    private static readonly Regex ControlKeyword = new(
        @"^\s*(?:if|else|for|foreach|while|do|switch|case|catch|try|finally|lock|using|return|throw|new|await|yield|var|let|fixed|checked|unchecked|goto|typeof|sizeof|nameof|when|elif|with|except|print|from|import)\b",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex CType = new(
        @"^\s*(?:\[[^\]]*\]\s*)*" + CModifiers + @"*(?:class|struct|interface|record|enum|namespace|module|trait|impl|union)\b\s*[\w.<>]",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex CFunction = new(
        @"^\s*(?:\[[^\]]*\]\s*)*" + CModifiers + @"*(?:[\w<>\[\],.?*&:]+\s+)+[\w~.]+\s*(?:<[^>()]*>)?\s*\([^;]*$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex JsFunction = new(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\b|^\s*(?:export\s+)?(?:const|let|var)\s+\w+\s*=\s*(?:async\s*)?(?:\([^)]*\)|\w+)\s*(?::\s*[^=]+)?=>|^\s*(?:(?:public|private|protected|static|async|get|set|readonly)\s+)*\w+\s*\([^;]*\)\s*(?::\s*[\w<>\[\]|, .]+)?\s*\{?\s*$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex CProperty = new(
        @"^\s*(?:\[[^\]]*\]\s*)*" + CModifiers + @"+[\w<>\[\],.?]+\s+\w+\s*(?:\{.*)?$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex PythonDeclaration = new(
        @"^\s*(?:async\s+)?def\s+\w+|^\s*class\s+\w+",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex VbDeclaration = new(
        @"^\s*(?:<[^>]*>\s*)*(?:(?:Public|Private|Protected|Friend|Shared|Overrides|Overridable|MustOverride|NotOverridable|Overloads|Shadows|Async|Iterator|Partial|ReadOnly|WriteOnly|Default|Static|MustInherit|NotInheritable)\s+)*(?:Sub|Function|Property|Class|Module|Structure|Interface|Enum|Namespace|Operator|Event)\s+\w+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    /// <summary>Gets a value indicating whether a line looks like a declaration header in the given language.</summary>
    /// <param name="line">The line.</param>
    /// <param name="languageId">The language identifier.</param>
    /// <returns><see langword="true"/> for function, method, property and type headers.</returns>
    public static bool LooksLikeDeclaration(string line, string? languageId)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 2 * MaxLength)
        {
            return false;
        }

        try
        {
            switch (LanguageFamily.Of(languageId))
            {
                case BlockStyle.Indentation:
                    return PythonDeclaration.IsMatch(line);
                case BlockStyle.Keywords:
                    return VbDeclaration.IsMatch(line) && !line.TrimStart().StartsWith("End ", StringComparison.OrdinalIgnoreCase);
                default:
                    var trimmed = line.TrimEnd();
                    if (trimmed.EndsWith(";", StringComparison.Ordinal) || ControlKeyword.IsMatch(line))
                    {
                        return false;
                    }

                    return CType.IsMatch(line) || CFunction.IsMatch(line) || JsFunction.IsMatch(line) || CProperty.IsMatch(line)
                        || PythonDeclaration.IsMatch(line);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Finds the declaration that encloses a caret.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="caretPosition">The caret position.</param>
    /// <param name="languageId">The language identifier.</param>
    /// <returns>The declaration line, trimmed, or <see langword="null"/> when none is found.</returns>
    public static string? FindEnclosingDeclaration(IDocumentText text, int caretPosition, string? languageId)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var caretLine = text.GetLineNumber(caretPosition);
        var lineStart = text.GetLineStart(caretLine);
        var beforeCaret = text.GetText(lineStart, caretPosition - lineStart);

        // Typing a header: the line itself is the declaration whose body comes next.
        if (LooksLikeDeclaration(beforeCaret, languageId))
        {
            return Clip(beforeCaret);
        }

        var caretLineText = text.GetLineText(caretLine);
        var limit = string.IsNullOrWhiteSpace(caretLineText)
            ? LanguageFamily.IndentWidth(beforeCaret)
            : LanguageFamily.IndentWidth(caretLineText);

        var lowest = Math.Max(0, caretLine - MaxLinesScanned);
        for (var line = caretLine - 1; line >= lowest; line--)
        {
            var candidate = text.GetLineText(line);
            if (string.IsNullOrWhiteSpace(candidate) || IsPunctuationOnly(candidate))
            {
                continue;
            }

            var indent = LanguageFamily.IndentWidth(candidate);
            if (indent < limit && LooksLikeDeclaration(candidate, languageId))
            {
                return Clip(candidate);
            }

            limit = Math.Min(limit, indent);
            if (limit == 0 && indent == 0 && !LooksLikeDeclaration(candidate, languageId))
            {
                // Nothing indented less than column 0 can enclose the caret.
                return null;
            }
        }

        return null;
    }

    /// <summary>Gets a value indicating whether a line holds only brackets, braces and separators.</summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> for lines such as <c>{</c>, <c>});</c> or <c>]</c>.</returns>
    public static bool IsPunctuationOnly(string line)
    {
        if (line is null)
        {
            throw new ArgumentNullException(nameof(line));
        }

        var any = false;
        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c is not ('{' or '}' or '(' or ')' or '[' or ']' or ';' or ','))
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    private static string Clip(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxLength ? trimmed : trimmed.Substring(0, MaxLength);
    }
}
