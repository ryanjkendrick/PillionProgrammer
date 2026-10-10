using System;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Core.InlineChat;

/// <summary>Where an inline chat scope came from (design section 5, Inline chat and refactoring).</summary>
public enum InlineScopeKind
{
    /// <summary>The selection, extended to whole lines.</summary>
    Selection,

    /// <summary>The member that encloses the caret (Roslyn, for C# and Visual Basic), with its doc comment.</summary>
    Member,

    /// <summary>The caret's line.</summary>
    Line,
}

/// <summary>
/// The code an inline chat prompt is about: whole lines, from the start of the first line to the end of the last line,
/// without the last line's line break. The proposal replaces exactly this text.
/// </summary>
public sealed class InlineScope
{
    /// <summary>Initializes a new instance of the <see cref="InlineScope"/> class.</summary>
    /// <param name="kind">Where the scope came from.</param>
    /// <param name="start">The position of the first line's first character.</param>
    /// <param name="end">The position after the last line's last character (before its line break).</param>
    /// <param name="startLine">The zero-based first line.</param>
    /// <param name="endLine">The zero-based last line.</param>
    public InlineScope(InlineScopeKind kind, int start, int end, int startLine, int endLine)
    {
        if (start < 0 || end < start || startLine < 0 || endLine < startLine)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        Kind = kind;
        Start = start;
        End = end;
        StartLine = startLine;
        EndLine = endLine;
    }

    /// <summary>Gets where the scope came from.</summary>
    public InlineScopeKind Kind { get; }

    /// <summary>Gets the position of the scope's first character.</summary>
    public int Start { get; }

    /// <summary>Gets the position after the scope's last character.</summary>
    public int End { get; }

    /// <summary>Gets the number of characters.</summary>
    public int Length => End - Start;

    /// <summary>Gets the zero-based first line.</summary>
    public int StartLine { get; }

    /// <summary>Gets the zero-based last line.</summary>
    public int EndLine { get; }

    /// <summary>Gets the number of lines.</summary>
    public int LineCount => EndLine - StartLine + 1;

    /// <summary>Gets a value indicating whether a zero-based line is one of the scope's lines.</summary>
    /// <param name="line">The line.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool ContainsLine(int line) => line >= StartLine && line <= EndLine;

    /// <inheritdoc/>
    public override string ToString() => Kind + " lines " + (StartLine + 1) + "-" + (EndLine + 1);
}

/// <summary>
/// Chooses the inline chat scope: the selection extended to whole lines; else the enclosing member (when the caller
/// found one, through Roslyn for C# and Visual Basic); else the caret's line.
/// </summary>
public static class InlineScopeSelector
{
    /// <summary>The most lines a member scope may have; a bigger member falls back to the caret's line.</summary>
    public const int MaxMemberLines = 400;

    /// <summary>Chooses the scope.</summary>
    /// <param name="text">The document text.</param>
    /// <param name="selectionStart">The selection's start position.</param>
    /// <param name="selectionEnd">The selection's end position (equal to the start when nothing is selected).</param>
    /// <param name="caret">The caret position.</param>
    /// <param name="memberStart">The enclosing member's start, or <see langword="null"/> when there is none.</param>
    /// <param name="memberEnd">The enclosing member's end, or <see langword="null"/>.</param>
    /// <returns>The scope.</returns>
    public static InlineScope Select(IDocumentText text, int selectionStart, int selectionEnd, int caret, int? memberStart = null, int? memberEnd = null)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var start = Clamp(Math.Min(selectionStart, selectionEnd), text.Length);
        var end = Clamp(Math.Max(selectionStart, selectionEnd), text.Length);
        if (end > start && !IsWhiteSpace(text, start, end))
        {
            return WholeLines(InlineScopeKind.Selection, text, start, end);
        }

        caret = Clamp(caret, text.Length);
        if (memberStart is { } ms && memberEnd is { } me && me > ms)
        {
            var member = WholeLines(InlineScopeKind.Member, text, Clamp(ms, text.Length), Clamp(me, text.Length));
            var caretLine = text.GetLineNumber(caret);
            if (member.ContainsLine(caretLine) && member.LineCount <= MaxMemberLines)
            {
                return member;
            }
        }

        var line = text.GetLineNumber(caret);
        return new InlineScope(InlineScopeKind.Line, text.GetLineStart(line), LineEnd(text, line), line, line);
    }

    /// <summary>
    /// Extends a span to whole lines. A span that ends at the start of a line (a selection of whole lines, which ends
    /// after the last line break) does not take that line.
    /// </summary>
    /// <param name="kind">The scope kind.</param>
    /// <param name="text">The document text.</param>
    /// <param name="start">The span's start.</param>
    /// <param name="end">The span's end.</param>
    /// <returns>The scope.</returns>
    public static InlineScope WholeLines(InlineScopeKind kind, IDocumentText text, int start, int end)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var startLine = text.GetLineNumber(start);
        var endLine = text.GetLineNumber(end);
        if (endLine > startLine && end == text.GetLineStart(endLine))
        {
            endLine--;
        }

        return new InlineScope(kind, text.GetLineStart(startLine), LineEnd(text, endLine), startLine, endLine);
    }

    private static int LineEnd(IDocumentText text, int line) => text.GetLineStart(line) + text.GetLineText(line).Length;

    private static int Clamp(int position, int length) => Math.Max(0, Math.Min(position, length));

    private static bool IsWhiteSpace(IDocumentText text, int start, int end)
    {
        var value = text.GetText(start, end - start);
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }
}
