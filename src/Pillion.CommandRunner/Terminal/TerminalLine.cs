namespace Pillion.CommandRunner.Terminal;

/// <summary>A run of cells on one line that share the same attributes.</summary>
/// <param name="Start">The first character of the run, as an index into <see cref="TerminalLine.Text"/>.</param>
/// <param name="Length">The number of characters in the run.</param>
/// <param name="Attributes">The attributes of every character in the run.</param>
public readonly record struct TerminalSpan(int Start, int Length, CellAttributes Attributes);

/// <summary>
/// One rendered line of the screen or the scrollback: its text with trailing blanks removed, and the attribute runs the
/// Terminal window colours it with. Immutable; a snapshot of the buffer at the time it was taken.
/// </summary>
public sealed class TerminalLine
{
    /// <summary>An empty line.</summary>
    public static readonly TerminalLine Empty = new(string.Empty, []);

    /// <summary>Initializes a new instance of the <see cref="TerminalLine"/> class.</summary>
    /// <param name="text">The text, trailing blanks removed.</param>
    /// <param name="spans">The attribute runs covering <paramref name="text"/>, in order; empty when every cell has the default attributes.</param>
    public TerminalLine(string text, IReadOnlyList<TerminalSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(spans);
        Text = text;
        Spans = spans;
    }

    /// <summary>Gets the text of the line, trailing blanks removed. Never contains control characters.</summary>
    public string Text { get; }

    /// <summary>Gets the attribute runs covering <see cref="Text"/>; empty when the whole line has the default attributes.</summary>
    public IReadOnlyList<TerminalSpan> Spans { get; }

    /// <inheritdoc/>
    public override string ToString() => Text;
}
