using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Read-only access to one immutable version of a document's text. The extension implements it over an editor
/// snapshot (which is safe to read from any thread); tests use <see cref="StringDocumentText"/>.
/// </summary>
public interface IDocumentText
{
    /// <summary>Gets the number of characters.</summary>
    int Length { get; }

    /// <summary>Gets the number of lines (at least 1).</summary>
    int LineCount { get; }

    /// <summary>Gets part of the text.</summary>
    /// <param name="start">The start position.</param>
    /// <param name="length">The number of characters.</param>
    /// <returns>The text.</returns>
    string GetText(int start, int length);

    /// <summary>Gets the zero-based line number that contains a position.</summary>
    /// <param name="position">A position from 0 to <see cref="Length"/>.</param>
    /// <returns>The line number.</returns>
    int GetLineNumber(int position);

    /// <summary>Gets the start position of a line.</summary>
    /// <param name="lineNumber">The zero-based line number.</param>
    /// <returns>The position of the line's first character.</returns>
    int GetLineStart(int lineNumber);

    /// <summary>Gets a line's text without its line break.</summary>
    /// <param name="lineNumber">The zero-based line number.</param>
    /// <returns>The text.</returns>
    string GetLineText(int lineNumber);
}

/// <summary>An <see cref="IDocumentText"/> over a string (tests, and documents read outside the editor).</summary>
public sealed class StringDocumentText : IDocumentText
{
    private readonly string _text;
    private readonly List<int> _lineStarts = [0];

    /// <summary>Initializes a new instance of the <see cref="StringDocumentText"/> class.</summary>
    /// <param name="text">The text. <c>\r\n</c>, <c>\n</c> and <c>\r</c> all end a line.</param>
    public StringDocumentText(string text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        for (var i = 0; i < _text.Length; i++)
        {
            if (_text[i] == '\r' && i + 1 < _text.Length && _text[i + 1] == '\n')
            {
                i++;
                _lineStarts.Add(i + 1);
            }
            else if (_text[i] is '\r' or '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    /// <inheritdoc/>
    public int Length => _text.Length;

    /// <inheritdoc/>
    public int LineCount => _lineStarts.Count;

    /// <inheritdoc/>
    public string GetText(int start, int length) => _text.Substring(start, length);

    /// <inheritdoc/>
    public int GetLineNumber(int position)
    {
        var index = _lineStarts.BinarySearch(position);
        return index >= 0 ? index : ~index - 1;
    }

    /// <inheritdoc/>
    public int GetLineStart(int lineNumber) => _lineStarts[lineNumber];

    /// <inheritdoc/>
    public string GetLineText(int lineNumber)
    {
        var start = _lineStarts[lineNumber];
        var end = lineNumber + 1 < _lineStarts.Count ? _lineStarts[lineNumber + 1] : _text.Length;
        while (end > start && _text[end - 1] is '\r' or '\n')
        {
            end--;
        }

        return _text.Substring(start, end - start);
    }
}
