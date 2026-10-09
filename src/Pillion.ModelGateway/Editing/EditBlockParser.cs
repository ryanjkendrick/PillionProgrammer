using System.Text.RegularExpressions;

namespace Pillion.ModelGateway.Editing;

/// <summary>One search-and-replace edit block from a model's reply.</summary>
/// <param name="Index">The block's position in the reply, from 1.</param>
/// <param name="SearchLines">The lines to find (line numbers copied from the prompt already removed).</param>
/// <param name="ReplaceLines">The lines that replace them; empty to delete.</param>
public sealed record EditBlock(int Index, IReadOnlyList<string> SearchLines, IReadOnlyList<string> ReplaceLines);

/// <summary>A block the parser could not read.</summary>
/// <param name="Index">The block's position in the reply, from 1.</param>
/// <param name="Reason">What is wrong, worded for the corrective message to the model.</param>
public sealed record MalformedEditBlock(int Index, string Reason);

/// <summary>The result of parsing a model's reply.</summary>
/// <param name="Blocks">The complete, well-formed blocks in reply order.</param>
/// <param name="Malformed">The blocks that could not be read.</param>
/// <param name="Explanation">The prose outside the blocks (fence lines removed), trimmed; empty when there is none.</param>
/// <param name="HasFenceOutsideBlocks">Whether the reply has a Markdown fence outside the blocks (code without edit markers when there are no blocks).</param>
/// <param name="Partial">
/// While a reply is still streaming: the block whose SEARCH part is complete and whose REPLACE part is arriving, with
/// the complete REPLACE lines so far; otherwise <see langword="null"/>.
/// </param>
public sealed record EditParseResult(
    IReadOnlyList<EditBlock> Blocks,
    IReadOnlyList<MalformedEditBlock> Malformed,
    string Explanation,
    bool HasFenceOutsideBlocks,
    EditBlock? Partial);

/// <summary>
/// Parses the search-and-replace edit format of the inline edit prompt (<c>Templates/inline-edit.json</c>):
/// <code>
/// &lt;&lt;&lt;&lt;&lt;&lt;&lt; SEARCH
/// lines copied exactly from the code
/// =======
/// the lines that replace them
/// &gt;&gt;&gt;&gt;&gt;&gt;&gt; REPLACE
/// </code>
/// Markers are whole lines (5 to 9 marker characters, the words optional and case-insensitive, surrounding whitespace
/// ignored), so Markdown fences inside a block are content and fences around the blocks are ignored. CRLF and LF
/// replies parse the same.
/// </summary>
public static partial class EditBlockParser
{
    private enum State
    {
        Outside,
        Search,
        Replace,
    }

    /// <summary>Parses a reply.</summary>
    /// <param name="reply">The reply text.</param>
    /// <param name="isComplete">
    /// <see langword="true"/> for the whole reply; <see langword="false"/> while it is still streaming, when an unfinished
    /// last line is ignored and an open block is reported as <see cref="EditParseResult.Partial"/> instead of malformed.
    /// </param>
    /// <returns>The blocks, the malformed blocks and the prose.</returns>
    public static EditParseResult Parse(string reply, bool isComplete = true)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var lines = EditText.SplitLines(EditText.ToLf(reply));

        // The text after the last newline is an unfinished line while streaming, and nothing when the reply ends with one.
        if (!isComplete || lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var blocks = new List<EditBlock>();
        var malformed = new List<MalformedEditBlock>();
        var prose = new List<string>();
        var hasFence = false;
        var state = State.Outside;
        var search = new List<string>();
        var replace = new List<string>();
        var index = 0;

        foreach (var line in lines)
        {
            var isStart = SearchMarker().IsMatch(line);
            var isDivider = DividerMarker().IsMatch(line);
            var isEnd = ReplaceMarker().IsMatch(line);
            switch (state)
            {
                case State.Outside:
                    if (isStart)
                    {
                        index++;
                        search.Clear();
                        replace.Clear();
                        state = State.Search;
                    }
                    else if (isEnd)
                    {
                        // A lone '=======' outside a block is prose (a Markdown heading underline); a lone end marker
                        // means the model left out the start of a block.
                        index++;
                        malformed.Add(new MalformedEditBlock(index, $"a '{line.Trim()}' line appears without a '<<<<<<< SEARCH' line before it"));
                    }
                    else if (FenceLine().IsMatch(line))
                    {
                        hasFence = true;
                    }
                    else
                    {
                        prose.Add(line);
                    }

                    break;

                case State.Search:
                    if (isDivider)
                    {
                        state = State.Replace;
                    }
                    else if (isStart)
                    {
                        malformed.Add(new MalformedEditBlock(index, "it has no '=======' and '>>>>>>> REPLACE' lines before the next '<<<<<<< SEARCH'"));
                        index++;
                        search.Clear();
                    }
                    else if (isEnd)
                    {
                        malformed.Add(new MalformedEditBlock(index, "it has no '=======' line between SEARCH and REPLACE"));
                        state = State.Outside;
                    }
                    else
                    {
                        search.Add(line);
                    }

                    break;

                case State.Replace:
                    if (isEnd)
                    {
                        blocks.Add(new EditBlock(index, EditText.StripLineNumbers([.. search]), EditText.StripLineNumbers([.. replace])));
                        state = State.Outside;
                    }
                    else if (isStart)
                    {
                        malformed.Add(new MalformedEditBlock(index, "it has no '>>>>>>> REPLACE' line before the next '<<<<<<< SEARCH'"));
                        index++;
                        search.Clear();
                        replace.Clear();
                        state = State.Search;
                    }
                    else
                    {
                        // A '=======' line here is content: only the first one divides a block.
                        replace.Add(line);
                    }

                    break;
            }
        }

        EditBlock? partial = null;
        if (state != State.Outside)
        {
            if (isComplete)
            {
                malformed.Add(new MalformedEditBlock(index, "it is not closed with a '>>>>>>> REPLACE' line (the reply may have been cut off)"));
            }
            else if (state == State.Replace)
            {
                partial = new EditBlock(index, EditText.StripLineNumbers([.. search]), EditText.StripLineNumbers([.. replace]));
            }
        }

        return new EditParseResult(blocks, malformed, string.Join('\n', prose).Trim(), hasFence, partial);
    }

    [GeneratedRegex(@"^\s*<{5,9}\s*(SEARCH)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchMarker();

    [GeneratedRegex(@"^\s*={5,9}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DividerMarker();

    [GeneratedRegex(@"^\s*>{5,9}\s*(REPLACE)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplaceMarker();

    [GeneratedRegex(@"^\s*(```+|~~~+)[\w+#.-]*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FenceLine();
}

/// <summary>A fenced code block in a reply.</summary>
/// <param name="Info">The info string after the opening fence (usually the language), or empty.</param>
/// <param name="Code">The code, LF line endings, without a trailing newline.</param>
/// <param name="IsClosed">Whether the closing fence was seen.</param>
public sealed record FencedCode(string Info, string Code, bool IsClosed);

/// <summary>Extracts Markdown fenced code blocks from a reply (used for <c>/test</c>, whose answer is new code).</summary>
public static partial class FencedCodeParser
{
    /// <summary>Extracts the fenced code blocks and the prose around them.</summary>
    /// <param name="reply">The reply.</param>
    /// <param name="isComplete">Whether the reply is complete; while streaming an unfinished last line is ignored.</param>
    /// <returns>The code blocks in order and the prose outside them, trimmed.</returns>
    public static (IReadOnlyList<FencedCode> Blocks, string Prose) Parse(string reply, bool isComplete = true)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var lines = EditText.SplitLines(EditText.ToLf(reply));
        if (!isComplete || lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var blocks = new List<FencedCode>();
        var prose = new List<string>();
        string? fence = null;
        var info = string.Empty;
        var code = new List<string>();
        foreach (var line in lines)
        {
            if (fence is null)
            {
                var open = Opening().Match(line);
                if (open.Success)
                {
                    fence = open.Groups["fence"].Value;
                    info = open.Groups["info"].Value.Trim();
                    code.Clear();
                }
                else
                {
                    prose.Add(line);
                }
            }
            else if (line.Trim() is { } trimmed && trimmed.Length >= fence.Length && trimmed.All(c => c == fence[0]))
            {
                blocks.Add(new FencedCode(info, string.Join('\n', code), IsClosed: true));
                fence = null;
            }
            else
            {
                code.Add(line);
            }
        }

        if (fence is not null)
        {
            blocks.Add(new FencedCode(info, string.Join('\n', code), IsClosed: false));
        }

        return (blocks, string.Join('\n', prose).Trim());
    }

    [GeneratedRegex(@"^\s*(?<fence>```+|~~~+)(?<info>[^`]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Opening();
}
