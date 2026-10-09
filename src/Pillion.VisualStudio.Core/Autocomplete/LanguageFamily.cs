using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>How a language delimits blocks, which decides the multi-line decision and the stop rules.</summary>
public enum BlockStyle
{
    /// <summary>Unknown: no block rules apply beyond the line limit and duplicate detection.</summary>
    None,

    /// <summary>Braces (C#, C, C++, Java, JavaScript, TypeScript and friends).</summary>
    Braces,

    /// <summary>Indentation (Python).</summary>
    Indentation,

    /// <summary>Keyword pairs such as <c>Sub</c> ... <c>End Sub</c> (Visual Basic).</summary>
    Keywords,
}

/// <summary>Maps a language identifier (as produced by <see cref="DocumentLanguage"/>) to its <see cref="BlockStyle"/>.</summary>
public static class LanguageFamily
{
    private static readonly HashSet<string> BraceLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "csharp", "c", "cpp", "java", "javascript", "typescript", "javascriptreact", "typescriptreact", "go", "rust",
        "swift", "kotlin", "php", "css", "scss", "less", "json", "powershell", "razor", "fsharp",
    };

    /// <summary>Gets the block style of a language.</summary>
    /// <param name="languageId">The language identifier.</param>
    /// <returns>The block style.</returns>
    public static BlockStyle Of(string? languageId)
    {
        if (string.IsNullOrEmpty(languageId))
        {
            return BlockStyle.None;
        }

        if (string.Equals(languageId, "python", StringComparison.OrdinalIgnoreCase))
        {
            return BlockStyle.Indentation;
        }

        if (string.Equals(languageId, "vb", StringComparison.OrdinalIgnoreCase))
        {
            return BlockStyle.Keywords;
        }

        return BraceLanguages.Contains(languageId!) ? BlockStyle.Braces : BlockStyle.None;
    }

    /// <summary>Gets the number of columns of a line's leading whitespace (a tab counts as four).</summary>
    /// <param name="line">The line.</param>
    /// <returns>The indentation width.</returns>
    public static int IndentWidth(string line)
    {
        if (line is null)
        {
            throw new ArgumentNullException(nameof(line));
        }

        var width = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                width++;
            }
            else if (c == '\t')
            {
                width += 4;
            }
            else
            {
                break;
            }
        }

        return width;
    }
}
