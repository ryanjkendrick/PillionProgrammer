namespace Pillion.ModelGateway.Editing;

/// <summary>How a block's SEARCH lines were found.</summary>
public enum EditMatchKind
{
    /// <summary>The lines are identical.</summary>
    Exact,

    /// <summary>The lines are identical apart from trailing whitespace.</summary>
    TrailingWhitespace,

    /// <summary>The lines are identical apart from leading and trailing whitespace; the replacement is re-indented to the code.</summary>
    Indentation,

    /// <summary>The lines were not found.</summary>
    NotFound,

    /// <summary>The lines were found more than once at the strictest level that found them.</summary>
    Ambiguous,

    /// <summary>The SEARCH part is empty (or blank) while the code is not.</summary>
    EmptySearch,
}

/// <summary>Where a block matched, or why it did not.</summary>
/// <param name="Kind">How the block matched.</param>
/// <param name="Line">The zero-based first line of the match (when it matched).</param>
/// <param name="LineCount">The number of lines matched.</param>
/// <param name="Occurrences">The number of places the SEARCH lines were found at the deciding level (0 when not found).</param>
/// <param name="Replacement">The lines to put in place of the match, re-indented for <see cref="EditMatchKind.Indentation"/>.</param>
public sealed record EditMatch(EditMatchKind Kind, int Line, int LineCount, int Occurrences, IReadOnlyList<string> Replacement)
{
    /// <summary>Gets a value indicating whether the block matched exactly one place.</summary>
    public bool IsMatch => Kind is EditMatchKind.Exact or EditMatchKind.TrailingWhitespace or EditMatchKind.Indentation;
}

/// <summary>A block that could not be applied.</summary>
/// <param name="Block">The block.</param>
/// <param name="Match">Why it did not match.</param>
public sealed record EditBlockFailure(EditBlock Block, EditMatch Match)
{
    /// <summary>Describes the failure for the corrective message to the model.</summary>
    /// <returns>A sentence.</returns>
    public string Describe() => Match.Kind switch
    {
        EditMatchKind.Ambiguous => $"Block {Block.Index}: its SEARCH lines match {Match.Occurrences} places; add surrounding lines so they match exactly one.",
        EditMatchKind.EmptySearch => $"Block {Block.Index}: its SEARCH part is empty; copy the lines to change.",
        _ => $"Block {Block.Index}: its SEARCH lines do not match the code; copy them exactly from the code, without line numbers.",
    };
}

/// <summary>The result of applying blocks to a text.</summary>
/// <param name="Text">The text with every matching block applied, LF line endings.</param>
/// <param name="Failures">The blocks that did not match exactly once, in order.</param>
/// <param name="Matches">How each applied block matched, in order.</param>
/// <param name="LastEditEndLine">The zero-based line just after the last applied replacement, or 0 when nothing was applied.</param>
public sealed record EditApplication(string Text, IReadOnlyList<EditBlockFailure> Failures, IReadOnlyList<EditMatch> Matches, int LastEditEndLine)
{
    /// <summary>Gets a value indicating whether every block applied.</summary>
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>
/// Applies search-and-replace blocks to an in-memory copy of a text (design section 8), one after another, each against
/// the text the previous ones produced. A block's SEARCH lines are found as whole lines, first exactly, then ignoring
/// trailing whitespace, then ignoring indentation (the replacement is re-indented to the code); a level that finds
/// them more than once makes the block ambiguous, and blank lines at the edges of a block that does not match are
/// dropped and the levels tried again. A block that does not match exactly once is a failure for the model to fix.
/// </summary>
public static class EditBlockApplier
{
    /// <summary>Applies blocks in order.</summary>
    /// <param name="text">The text, any line endings (the result has LF endings).</param>
    /// <param name="blocks">The blocks.</param>
    /// <returns>The edited text and any failures.</returns>
    public static EditApplication Apply(string text, IEnumerable<EditBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(blocks);

        var lines = EditText.SplitLines(EditText.ToLf(text));
        var failures = new List<EditBlockFailure>();
        var matches = new List<EditMatch>();
        var lastEnd = 0;
        foreach (var block in blocks)
        {
            var match = Find(lines, block.SearchLines, block.ReplaceLines);
            if (!match.IsMatch)
            {
                failures.Add(new EditBlockFailure(block, match));
                continue;
            }

            lines.RemoveRange(match.Line, match.LineCount);
            lines.InsertRange(match.Line, match.Replacement);
            lastEnd = match.Line + match.Replacement.Count;
            matches.Add(match);
        }

        return new EditApplication(string.Join('\n', lines), failures, matches, lastEnd);
    }

    /// <summary>Finds where SEARCH lines match.</summary>
    /// <param name="lines">The text's lines.</param>
    /// <param name="search">The SEARCH lines.</param>
    /// <param name="replace">The REPLACE lines.</param>
    /// <returns>The match, with the replacement to use.</returns>
    public static EditMatch Find(IReadOnlyList<string> lines, IReadOnlyList<string> search, IReadOnlyList<string> replace)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(replace);

        if (search.All(l => l.Trim().Length == 0))
        {
            // An empty SEARCH can only mean "the whole text", which is only unambiguous when the text is blank.
            return lines.All(l => l.Trim().Length == 0)
                ? new EditMatch(EditMatchKind.Exact, 0, lines.Count, 1, [.. replace])
                : new EditMatch(EditMatchKind.EmptySearch, 0, 0, 0, []);
        }

        var match = FindAtLevels(lines, search, replace);
        if (match.Kind != EditMatchKind.NotFound)
        {
            return match;
        }

        // Models often add or drop a blank line at the edges of a block: drop blank edge lines from SEARCH, and the
        // same number of blank edge lines from REPLACE, and try again.
        var leading = search.TakeWhile(l => l.Trim().Length == 0).Count();
        var trailing = search.Reverse().TakeWhile(l => l.Trim().Length == 0).Count();
        if (leading + trailing == 0)
        {
            return match;
        }

        var trimmedSearch = search.Skip(leading).Take(search.Count - leading - trailing).ToList();
        var replaceLeading = Math.Min(leading, replace.TakeWhile(l => l.Trim().Length == 0).Count());
        var replaceTrailing = Math.Min(trailing, replace.Skip(replaceLeading).Reverse().TakeWhile(l => l.Trim().Length == 0).Count());
        var trimmedReplace = replace.Skip(replaceLeading).Take(replace.Count - replaceLeading - replaceTrailing).ToList();
        return FindAtLevels(lines, trimmedSearch, trimmedReplace);
    }

    private static EditMatch FindAtLevels(IReadOnlyList<string> lines, IReadOnlyList<string> search, IReadOnlyList<string> replace)
    {
        foreach (var kind in (EditMatchKind[])[EditMatchKind.Exact, EditMatchKind.TrailingWhitespace, EditMatchKind.Indentation])
        {
            var positions = Positions(lines, search, kind);
            if (positions.Count > 1)
            {
                return new EditMatch(EditMatchKind.Ambiguous, positions[0], search.Count, positions.Count, []);
            }

            if (positions.Count == 1)
            {
                var replacement = kind == EditMatchKind.Indentation ? Reindent(lines, positions[0], search, replace) : [.. replace];
                return new EditMatch(kind, positions[0], search.Count, 1, replacement);
            }
        }

        return new EditMatch(EditMatchKind.NotFound, 0, 0, 0, []);
    }

    private static List<int> Positions(IReadOnlyList<string> lines, IReadOnlyList<string> search, EditMatchKind kind)
    {
        var positions = new List<int>();
        for (var start = 0; start + search.Count <= lines.Count; start++)
        {
            var all = true;
            for (var i = 0; i < search.Count && all; i++)
            {
                all = Same(lines[start + i], search[i], kind);
            }

            if (all)
            {
                positions.Add(start);
            }
        }

        return positions;
    }

    private static bool Same(string line, string search, EditMatchKind kind) => kind switch
    {
        EditMatchKind.Exact => string.Equals(line, search, StringComparison.Ordinal),
        EditMatchKind.TrailingWhitespace => string.Equals(line.TrimEnd(), search.TrimEnd(), StringComparison.Ordinal),
        _ => string.Equals(line.Trim(), search.Trim(), StringComparison.Ordinal),
    };

    /// <summary>Moves the replacement from the SEARCH lines' indentation to the code's, line by line.</summary>
    private static List<string> Reindent(IReadOnlyList<string> lines, int position, IReadOnlyList<string> search, IReadOnlyList<string> replace)
    {
        var first = 0;
        while (first < search.Count && search[first].Trim().Length == 0)
        {
            first++;
        }

        var searchIndent = EditText.LeadingWhitespace(search[first]);
        var codeIndent = EditText.LeadingWhitespace(lines[position + first]);
        return replace.Select(line =>
        {
            if (line.Trim().Length == 0)
            {
                return string.Empty;
            }

            if (line.StartsWith(searchIndent, StringComparison.Ordinal))
            {
                return codeIndent + line[searchIndent.Length..];
            }

            // Less indented than the SEARCH lines: keep the same offset from the code's indentation where possible.
            var own = EditText.LeadingWhitespace(line);
            var shift = codeIndent.Length - searchIndent.Length;
            return shift >= 0
                ? codeIndent[..Math.Min(shift, codeIndent.Length)] + line
                : line[Math.Min(-shift, own.Length)..];
        }).ToList();
    }
}
