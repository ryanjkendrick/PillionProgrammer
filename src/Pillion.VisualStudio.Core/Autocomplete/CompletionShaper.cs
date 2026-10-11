using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>Why the shaper stopped taking text from the stream.</summary>
public enum CompletionStopReason
{
    /// <summary>Not stopped: the stream may continue.</summary>
    None,

    /// <summary>The stream ended.</summary>
    EndOfStream,

    /// <summary>Single-line request: the first line ended.</summary>
    SingleLine,

    /// <summary>The output closed the block that encloses the caret (brace matching for C-family languages).</summary>
    BlockEnd,

    /// <summary>The output dedented out of the block that encloses the caret (Python, Visual Basic <c>End</c>).</summary>
    Dedent,

    /// <summary>A line duplicated text already below the caret.</summary>
    Duplicate,

    /// <summary>The line limit (30) was reached.</summary>
    MaxLines,

    /// <summary>A Markdown fence line (<c>```csharp</c>, <c>```</c>) followed content: the model was writing a chat reply.</summary>
    Fence,
}

/// <summary>What the shaper needs to know about the document around the caret.</summary>
public sealed class ShapingContext
{
    /// <summary>The default line limit for multi-line completions.</summary>
    public const int DefaultMaxLines = 30;

    /// <summary>Gets or sets the language identifier.</summary>
    public string? LanguageId { get; set; }

    /// <summary>Gets or sets a value indicating whether the completion may span lines.</summary>
    public bool MultiLine { get; set; }

    /// <summary>Gets or sets the caret line's text before the caret, including virtual-space indentation.</summary>
    public string LineBeforeCaret { get; set; } = string.Empty;

    /// <summary>Gets or sets the caret line's text after the caret.</summary>
    public string LineAfterCaret { get; set; } = string.Empty;

    /// <summary>Gets or sets the lines below the caret line (a few are enough), used for duplicates and block ends.</summary>
    public IReadOnlyList<string> LinesBelow { get; set; } = [];

    /// <summary>Gets or sets the most lines a multi-line completion may have.</summary>
    public int MaxLines { get; set; } = DefaultMaxLines;

    /// <summary>
    /// Gets or sets a value indicating whether the completion ends with the first block it opens and closes: the body
    /// that follows a declaration signature (brace languages). Without it the model tends to go on to the next member.
    /// </summary>
    public bool StopAfterFirstBlock { get; set; }
}

/// <summary>
/// Applies the output stop rules of design section 5 (Completion shaping) to streamed completion text: single-line
/// requests end at the first line break; multi-line output is cut at the end of the enclosing block (brace matching
/// for C-family languages, dedent for Python), at 30 lines, or at the first line that duplicates text already below
/// the caret. A line that is only a Markdown fence is never shown (Gate 1 open issue 2; the host strips fences too):
/// a leading one is skipped, a later one ends the suggestion, and a single-line suggestion that is a fence is empty.
/// Feed chunks to <see cref="Append"/> until it returns <see langword="true"/> or the stream ends, then call
/// <see cref="Complete"/>; <see cref="DisplayText"/> is what may be shown meanwhile. Not thread-safe.
/// </summary>
public sealed class CompletionShaper
{
    private const int DuplicateLookahead = 3;

    private readonly ShapingContext _context;
    private readonly BlockStyle _style;
    private readonly StringBuilder _pending = new();
    private readonly List<string> _lines = [];
    private readonly string[] _below;
    private readonly bool _closerBelow;
    private readonly int _blockIndent;
    private readonly BraceScanner _braces = new();
    private string? _final;

    /// <summary>Initializes a new instance of the <see cref="CompletionShaper"/> class.</summary>
    /// <param name="context">The document context.</param>
    public CompletionShaper(ShapingContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _style = LanguageFamily.Of(context.LanguageId);
        _below = context.LinesBelow
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Take(DuplicateLookahead)
            .ToArray();
        _closerBelow = context.LineAfterCaret.Trim().StartsWith("}", StringComparison.Ordinal)
            || (_below.Length > 0 && _below[0].StartsWith("}", StringComparison.Ordinal));

        var before = context.LineBeforeCaret;
        var indent = LanguageFamily.IndentWidth(before);
        _blockIndent = before.TrimEnd().EndsWith(":", StringComparison.Ordinal) || OpensVbBlock(before)
            ? indent + 1
            : indent;
    }

    /// <summary>Gets why the shaper stopped, or <see cref="CompletionStopReason.None"/> while it is still taking text.</summary>
    public CompletionStopReason StopReason { get; private set; }

    /// <summary>Gets a value indicating whether the shaper needs no more text.</summary>
    public bool IsStopped => StopReason != CompletionStopReason.None;

    /// <summary>Gets the number of complete lines accepted so far that have content.</summary>
    public int CompleteContentLines { get; private set; }

    /// <summary>
    /// Gets the accepted complete lines so far (no partial last line), for showing a multi-line suggestion before the
    /// stream ends, or <see langword="null"/> when no complete line has content yet.
    /// </summary>
    public string? PartialText => CompleteContentLines == 0 ? null : Join(_lines);

    /// <summary>
    /// Gets what may be shown before the stream ends, or <see langword="null"/> when nothing visible is known yet: for a
    /// multi-line request the complete lines (<see cref="PartialText"/>), for a single-line one the text so far. A
    /// single-line text is held back while it could still become a fence (it starts with a backtick or a tilde), and a
    /// tail that could be the start of the text right of the caret is held until it is known not to repeat it.
    /// </summary>
    public string? DisplayText
    {
        get
        {
            if (_final is not null)
            {
                return _final.Trim().Length == 0 ? null : _final;
            }

            if (_context.MultiLine)
            {
                return PartialText;
            }

            if (_lines.Count > 0)
            {
                var line = _lines[0];
                return line.Trim().Length == 0 ? null : line;
            }

            var pending = _pending.ToString();
            var start = pending.TrimStart();
            if (start.Length == 0 || start[0] is '`' or '~')
            {
                return null;
            }

            var shown = HoldSuffixStart(pending);
            return shown.Trim().Length == 0 ? null : shown;
        }
    }

    /// <summary>Gets a value indicating whether a line is a Markdown fence: <c>```</c> or <c>~~~</c>, optionally followed by a language tag.</summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> for a fence line.</returns>
    public static bool IsFenceLine(string? line)
    {
        var trimmed = (line ?? string.Empty).Trim();
        if (!(trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (var c in trimmed.TrimStart('`', '~'))
        {
            if (!(char.IsLetterOrDigit(c) || c is '+' or '#' or '-' or '_' or '.' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Shapes a whole completion at once.</summary>
    /// <param name="context">The document context.</param>
    /// <param name="text">The model output.</param>
    /// <returns>The shaped text (empty when nothing is left).</returns>
    public static string Shape(ShapingContext context, string text)
    {
        var shaper = new CompletionShaper(context);
        shaper.Append(text);
        return shaper.Complete();
    }

    /// <summary>
    /// Gets the single-line form of a multi-line completion: everything up to the end of its first line with content.
    /// </summary>
    /// <param name="text">A shaped completion.</param>
    /// <returns>The single-line form, or <see langword="null"/> when it is the same as <paramref name="text"/>.</returns>
    public static string? FirstLineVariant(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var lines = text.Split('\n');
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines[i]);
            if (lines[i].Trim().Length > 0)
            {
                var result = builder.ToString();
                return result.Length == text.Length ? null : result;
            }
        }

        return null;
    }

    /// <summary>Takes the next chunk of the stream. Line breaks are normalised to <c>\n</c>.</summary>
    /// <param name="chunk">The text.</param>
    /// <returns><see langword="true"/> when a stop rule fired and no more text is needed.</returns>
    public bool Append(string? chunk)
    {
        if (IsStopped || string.IsNullOrEmpty(chunk))
        {
            return IsStopped;
        }

        foreach (var c in chunk!)
        {
            if (c == '\r')
            {
                continue; // \r\n and \n end a line alike; a lone \r is dropped.
            }

            if (c != '\n')
            {
                _pending.Append(c);
                continue;
            }

            if (!_context.MultiLine)
            {
                var single = _pending.ToString();
                _pending.Clear();
                if (IsFenceLine(single))
                {
                    StopReason = CompletionStopReason.Fence;
                    return true;
                }

                AcceptLine(single);
                StopReason = CompletionStopReason.SingleLine;
                return true;
            }

            var line = _pending.ToString();
            _pending.Clear();
            if (ProcessLine(line, complete: true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ends the stream (if no stop rule fired) and returns the shaped completion.</summary>
    /// <returns>The completion with <c>\n</c> line breaks and no trailing blank lines; empty when nothing is left.</returns>
    public string Complete()
    {
        if (_final is not null)
        {
            return _final;
        }

        if (!IsStopped)
        {
            if (_pending.Length > 0)
            {
                if (IsFenceLine(_pending.ToString()))
                {
                    // A fence that the stream ended on is never shown.
                }
                else if (_context.MultiLine)
                {
                    ProcessLine(_pending.ToString(), complete: false);
                }
                else
                {
                    AcceptLine(_pending.ToString());
                }

                _pending.Clear();
            }

            if (!IsStopped)
            {
                StopReason = CompletionStopReason.EndOfStream;
            }
        }

        _final = TrimSuffixOverlap(Join(_lines));
        return _final;
    }

    private static bool OpensVbBlock(string before)
    {
        var trimmed = before.Trim();
        return trimmed.EndsWith(" Then", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Sub ", StringComparison.OrdinalIgnoreCase)
            || trimmed.IndexOf(" Sub ", StringComparison.OrdinalIgnoreCase) >= 0
            || trimmed.IndexOf(" Function ", StringComparison.OrdinalIgnoreCase) >= 0
            || trimmed.StartsWith("Function ", StringComparison.OrdinalIgnoreCase);
    }

    private static string Join(List<string> lines)
    {
        var count = lines.Count;
        while (count > 0 && lines[count - 1].Trim().Length == 0)
        {
            count--;
        }

        if (count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(i == count - 1 ? lines[i].TrimEnd() : lines[i]);
        }

        return builder.ToString();
    }

    /// <summary>Handles one line of multi-line output. Returns true when a stop rule fired.</summary>
    private bool ProcessLine(string line, bool complete)
    {
        var index = _lines.Count;
        var trimmed = line.Trim();

        // A Markdown fence: skipped before any content, the end of the suggestion after it.
        if (IsFenceLine(trimmed))
        {
            if (CompleteContentLines == 0 && !_lines.Any(l => l.Trim().Length > 0))
            {
                return false;
            }

            StopReason = CompletionStopReason.Fence;
            return true;
        }

        // Duplicate of the code already below the caret: the model is re-typing what is there.
        if (trimmed.Length > 0 && !DeclarationHeuristic.IsPunctuationOnly(trimmed))
        {
            var candidate = index == 0 ? (_context.LineBeforeCaret + line).Trim() : trimmed;
            if (_below.Contains(candidate, StringComparer.Ordinal))
            {
                StopReason = CompletionStopReason.Duplicate;
                return true;
            }
        }

        // Dedent out of the enclosing block (Python; Visual Basic End statements).
        if (index > 0 && trimmed.Length > 0
            && (_style == BlockStyle.Indentation
                || (_style == BlockStyle.Keywords && trimmed.StartsWith("End", StringComparison.OrdinalIgnoreCase)))
            && LanguageFamily.IndentWidth(line) < _blockIndent)
        {
            StopReason = CompletionStopReason.Dedent;
            return true;
        }

        // Brace matching: the output closed the block that encloses the caret.
        if (_style is BlockStyle.Braces or BlockStyle.None)
        {
            var close = _braces.FindEnclosingClose(line, _context.StopAfterFirstBlock, out var firstBlockClosed);
            if (firstBlockClosed)
            {
                // The body that follows the signature is complete.
                AcceptLine(line.Substring(0, close + 1));
                StopReason = CompletionStopReason.BlockEnd;
                return true;
            }

            if (close >= 0)
            {
                var head = line.Substring(0, close);
                if (_closerBelow)
                {
                    // The closing brace is already in the document: keep what comes before it on this line, if anything.
                    if (head.Trim().Length > 0)
                    {
                        AcceptLine(head.TrimEnd());
                    }
                }
                else
                {
                    AcceptLine(line.Substring(0, close + 1));
                }

                StopReason = CompletionStopReason.BlockEnd;
                return true;
            }
        }

        AcceptLine(line, complete);
        if (_lines.Count >= _context.MaxLines)
        {
            StopReason = CompletionStopReason.MaxLines;
            return true;
        }

        return false;
    }

    private void AcceptLine(string line, bool complete = true)
    {
        _lines.Add(line);
        if (complete && line.Trim().Length > 0)
        {
            CompleteContentLines++;
        }
    }

    /// <summary>Holds back the longest tail of a partial line that is the start of the text right of the caret.</summary>
    private string HoldSuffixStart(string text)
    {
        var after = _context.LineAfterCaret.Trim();
        for (var length = Math.Min(after.Length, text.Length); length > 0; length--)
        {
            if (string.CompareOrdinal(text, text.Length - length, after, 0, length) == 0)
            {
                return text.Substring(0, text.Length - length);
            }
        }

        return text;
    }

    /// <summary>Drops a tail that repeats the text right of the caret (for example <c>);</c> inside a call).</summary>
    private string TrimSuffixOverlap(string text)
    {
        var after = _context.LineAfterCaret.Trim();
        if (after.Length == 0 || text.Length == 0)
        {
            return text;
        }

        var trimmed = text.TrimEnd();
        if (trimmed.EndsWith(after, StringComparison.Ordinal))
        {
            return trimmed.Substring(0, trimmed.Length - after.Length).TrimEnd();
        }

        return text;
    }

    /// <summary>
    /// Tracks brace depth across lines of C-family output, skipping strings, character literals and comments, and
    /// reports the first closing brace that takes the depth below the caret's block.
    /// </summary>
    private sealed class BraceScanner
    {
        private int _depth;
        private bool _openedBlock;
        private bool _inBlockComment;
        private char _stringQuote;
        private bool _verbatim;

        /// <summary>
        /// Scans a line; returns the index of the brace that closes the caret's block, or of the brace that closes the
        /// first block the output opened when <paramref name="stopAfterFirstBlock"/> is set (then
        /// <paramref name="firstBlockClosed"/> is true), or -1.
        /// </summary>
        public int FindEnclosingClose(string line, bool stopAfterFirstBlock, out bool firstBlockClosed)
        {
            firstBlockClosed = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (_inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        _inBlockComment = false;
                        i++;
                    }

                    continue;
                }

                if (_stringQuote != '\0')
                {
                    if (_verbatim && c == '"' && next == '"')
                    {
                        i++;
                    }
                    else if (!_verbatim && c == '\\')
                    {
                        i++;
                    }
                    else if (c == _stringQuote)
                    {
                        _stringQuote = '\0';
                        _verbatim = false;
                    }

                    continue;
                }

                switch (c)
                {
                    case '/' when next == '/':
                        return -1; // Line comment: the rest of the line does not count.
                    case '/' when next == '*':
                        _inBlockComment = true;
                        i++;
                        break;
                    case '@' when next == '"':
                        _stringQuote = '"';
                        _verbatim = true;
                        i++;
                        break;
                    case '"' or '\'' or '`':
                        _stringQuote = c;
                        break;
                    case '{':
                        _depth++;
                        _openedBlock = true;
                        break;
                    case '}':
                        _depth--;
                        if (_depth < 0)
                        {
                            return i;
                        }

                        if (_depth == 0 && _openedBlock && stopAfterFirstBlock)
                        {
                            firstBlockClosed = true;
                            return i;
                        }

                        break;
                }
            }

            // Strings other than verbatim and template literals end at the line end.
            if (_stringQuote is '"' or '\'' && !_verbatim)
            {
                _stringQuote = '\0';
            }

            return -1;
        }
    }
}
