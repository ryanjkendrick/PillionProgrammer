namespace Pillion.ContextEngine.Chunking.Core;

/// <summary>
/// A file's text with a line index. Lines end at <c>\r\n</c>, <c>\r</c>, <c>\n</c>, U+0085, U+2028 or U+2029, the same
/// line breaks Roslyn and the Visual Studio editor recognise, so line numbers agree with what the user sees. A line
/// break at the very end of the text does not start another line. All chunkers map syntax positions (character
/// offsets) through this index, so every language numbers lines the same way.
/// </summary>
internal sealed class SourceDocument
{
    private readonly int[] _lineStarts;
    private readonly int[] _lineEnds;

    public SourceDocument(string path, string text)
    {
        Path = path ?? string.Empty;
        Text = text ?? string.Empty;

        var starts = new List<int>(Math.Max(16, Text.Length / 32));
        var ends = new List<int>(starts.Capacity);
        var lineStart = 0;
        var i = 0;
        while (i < Text.Length)
        {
            var c = Text[i];
            if (c is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029')
            {
                starts.Add(lineStart);
                ends.Add(i);
                i += c == '\r' && i + 1 < Text.Length && Text[i + 1] == '\n' ? 2 : 1;
                lineStart = i;
            }
            else
            {
                i++;
            }
        }

        if (lineStart < Text.Length)
        {
            starts.Add(lineStart);
            ends.Add(Text.Length);
        }

        _lineStarts = [.. starts];
        _lineEnds = [.. ends];
        IsBlank = Text.AsSpan().IsWhiteSpace();
    }

    /// <summary>The path used in breadcrumbs.</summary>
    public string Path { get; }

    /// <summary>The whole text.</summary>
    public string Text { get; }

    /// <summary>The number of lines; 0 for empty text.</summary>
    public int LineCount => _lineStarts.Length;

    /// <summary>True when the text is empty or only white space.</summary>
    public bool IsBlank { get; }

    /// <summary>The offset of the first character of a 1-based line.</summary>
    public int LineStart(int line) => _lineStarts[line - 1];

    /// <summary>The offset just past the last character of a 1-based line, before its line break.</summary>
    public int LineEnd(int line) => _lineEnds[line - 1];

    /// <summary>The characters of a line without its line break.</summary>
    public ReadOnlySpan<char> LineSpan(int line) => Text.AsSpan(LineStart(line), LineEnd(line) - LineStart(line));

    /// <summary>The text of lines <paramref name="startLine"/> to <paramref name="endLine"/> without the final line break.</summary>
    public string Slice(int startLine, int endLine) => Text[LineStart(startLine)..LineEnd(endLine)];

    /// <summary>True when the line is empty or only white space.</summary>
    public bool IsBlankLine(int line) => LineSpan(line).IsWhiteSpace();

    /// <summary>
    /// The 1-based line holding a character offset. An offset on a line break belongs to the line it ends; offsets
    /// past the end clamp to the last line, negative ones to the first.
    /// </summary>
    public int LineOfOffset(int offset)
    {
        if (_lineStarts.Length == 0)
        {
            return 1;
        }

        if (offset <= 0)
        {
            return 1;
        }

        var index = Array.BinarySearch(_lineStarts, offset);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return Math.Clamp(index + 1, 1, _lineStarts.Length);
    }

    /// <summary>The line of the last character of a span; for an empty span, the line of its start.</summary>
    public int LastLineOfSpan(int start, int end) => LineOfOffset(end > start ? end - 1 : start);
}
