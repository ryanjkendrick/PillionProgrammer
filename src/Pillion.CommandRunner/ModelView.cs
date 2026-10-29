using System.Text;
using Pillion.CommandRunner.Terminal;
using Pillion.Common.Redaction;

namespace Pillion.CommandRunner;

/// <summary>
/// What the model sees of a terminal buffer (design section 8): the rendered lines, tail-truncated to a byte cap so
/// the end of the output (where the errors and the summary are) survives, and passed through the secret scanner. The
/// full buffer stays in the session for the Terminal window.
/// </summary>
public sealed class ModelView
{
    /// <summary>The bytes reserved for the omission note when a view is truncated.</summary>
    private const int NoteReserve = 160;

    private ModelView(string text, int totalLines, int includedLines, bool truncated, bool redacted, int byteCount)
    {
        Text = text;
        TotalLines = totalLines;
        IncludedLines = includedLines;
        Truncated = truncated;
        Redacted = redacted;
        ByteCount = byteCount;
    }

    /// <summary>Gets the text: lines separated by <c>\n</c>, a note on the first line when earlier lines were omitted, secrets replaced by <c>&lt;redacted:kind&gt;</c>.</summary>
    public string Text { get; }

    /// <summary>Gets the number of lines the buffer held for this range.</summary>
    public int TotalLines { get; }

    /// <summary>Gets the number of lines in <see cref="Text"/>, the note excluded.</summary>
    public int IncludedLines { get; }

    /// <summary>Gets the number of lines omitted from the start.</summary>
    public int OmittedLines => TotalLines - IncludedLines;

    /// <summary>Gets a value indicating whether lines were omitted to respect the byte cap.</summary>
    public bool Truncated { get; }

    /// <summary>Gets a value indicating whether the secret scanner replaced anything (or failed closed).</summary>
    public bool Redacted { get; }

    /// <summary>Gets the UTF-8 size of <see cref="Text"/> before redaction.</summary>
    public int ByteCount { get; }

    /// <summary>Builds the view of a terminal's lines.</summary>
    /// <param name="lines">The lines, oldest first (a run's range, or a buffer's tail).</param>
    /// <param name="byteCap">The maximum UTF-8 size of the text, including the note; at least 256.</param>
    /// <returns>The view.</returns>
    public static ModelView Build(IReadOnlyList<TerminalLine> lines, int byteCap)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteCap, 256);

        // Walk from the end, keeping whole lines while they fit; the first line kept gets the remaining budget minus the note.
        var budget = byteCap;
        var first = lines.Count;
        var used = 0;
        while (first > 0)
        {
            var size = Encoding.UTF8.GetByteCount(lines[first - 1].Text) + (first < lines.Count ? 1 : 0);
            var reserve = first - 1 > 0 ? NoteReserve : 0;
            if (used + size + reserve > budget)
            {
                break;
            }

            used += size;
            first--;
        }

        var truncated = first > 0;
        var builder = new StringBuilder(used + (truncated ? NoteReserve : 0));
        if (truncated)
        {
            builder.Append("[... ").Append(first).Append(first == 1 ? " earlier line" : " earlier lines")
                .Append(" omitted; the full output is in the Terminal window ...]");
        }

        for (var index = first; index < lines.Count; index++)
        {
            if (builder.Length > 0 || index > first)
            {
                builder.Append('\n');
            }

            builder.Append(lines[index].Text);
        }

        var text = builder.ToString();
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var redacted = LogRedactor.Redact(text);
        return new ModelView(redacted, lines.Count, lines.Count - first, truncated, !ReferenceEquals(redacted, text), byteCount);
    }

    /// <summary>Builds the view of a buffer's tail: everything from a history index to the last used row.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="fromLine">The first history index; older lines are left out.</param>
    /// <param name="byteCap">The byte cap.</param>
    /// <returns>The view.</returns>
    public static ModelView Build(ITerminalBuffer buffer, long fromLine, int byteCap)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Build(buffer.GetLinesToEnd(fromLine), byteCap);
    }

    /// <inheritdoc/>
    public override string ToString() => Text;
}
