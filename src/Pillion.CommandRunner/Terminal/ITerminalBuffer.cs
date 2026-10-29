namespace Pillion.CommandRunner.Terminal;

/// <summary>A snapshot of the screen for the Terminal window: the rows, the cursor and the title at one generation.</summary>
/// <param name="Generation">The buffer generation the snapshot was taken at.</param>
/// <param name="Columns">The screen width.</param>
/// <param name="Rows">The screen rows, top to bottom, always <c>Rows</c> of them.</param>
/// <param name="CursorRow">The cursor row, 0-based.</param>
/// <param name="CursorColumn">The cursor column, 0-based.</param>
/// <param name="CursorVisible">Whether the application wants the cursor shown.</param>
/// <param name="FirstRowIndex">The history index of the first row (lines before it are scrollback).</param>
/// <param name="OldestLineIndex">The history index of the oldest scrollback line still held.</param>
/// <param name="Title">The window title set by the application, if any.</param>
/// <param name="AlternateScreen">Whether a full-screen application has switched to the alternate screen.</param>
public sealed record TerminalSnapshot(
    long Generation,
    int Columns,
    IReadOnlyList<TerminalLine> Rows,
    int CursorRow,
    int CursorColumn,
    bool CursorVisible,
    long FirstRowIndex,
    long OldestLineIndex,
    string? Title,
    bool AlternateScreen);

/// <summary>
/// The full terminal buffer of a session or a background process: the screen and its scrollback, as the Terminal
/// window shows them (design section 5). The model never sees this directly; it gets a <c>ModelView</c> built from it.
/// Thread-safe.
/// </summary>
/// <remarks>
/// Lines have <b>history indices</b>: every line that scrolled off the top of the screen got the next index and the
/// screen rows follow them, so <see cref="FirstRowIndex"/> is the index of the top screen row and
/// <see cref="EndLineIndex"/> one past the bottom row. Lines older than <see cref="OldestLineIndex"/> have left the
/// bounded scrollback. A consumer that renders deltas keeps the last <see cref="Generation"/> and
/// <see cref="FirstRowIndex"/> it saw and asks for the lines between.
/// </remarks>
public interface ITerminalBuffer
{
    /// <summary>Raised after the buffer changed. Raised on the output pump thread; handlers must return quickly and must not call back into the buffer while holding other locks.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the screen width in cells.</summary>
    int Columns { get; }

    /// <summary>Gets the screen height in rows.</summary>
    int Rows { get; }

    /// <summary>Gets the number of scrollback lines kept.</summary>
    int ScrollbackLimit { get; }

    /// <summary>Gets a counter that increases with every change; equal values mean an unchanged buffer.</summary>
    long Generation { get; }

    /// <summary>Gets the history index of the top screen row.</summary>
    long FirstRowIndex { get; }

    /// <summary>Gets the history index one past the bottom screen row.</summary>
    long EndLineIndex { get; }

    /// <summary>Gets the history index of the oldest scrollback line still held.</summary>
    long OldestLineIndex { get; }

    /// <summary>Gets the time of the last change, or <see langword="null"/> before the first.</summary>
    DateTimeOffset? LastChangedAt { get; }

    /// <summary>Gets the window title the application set, if any.</summary>
    string? Title { get; }

    /// <summary>Takes a snapshot of the screen.</summary>
    /// <returns>The snapshot.</returns>
    TerminalSnapshot Snapshot();

    /// <summary>Gets the lines in a history range (scrollback and screen), oldest first; lines that have left the scrollback are skipped.</summary>
    /// <param name="from">The first history index.</param>
    /// <param name="toExclusive">One past the last history index.</param>
    /// <returns>The lines.</returns>
    IReadOnlyList<TerminalLine> GetLines(long from, long toExclusive);

    /// <summary>Gets the lines from a history index to the last used screen row.</summary>
    /// <param name="from">The first history index.</param>
    /// <returns>The lines.</returns>
    IReadOnlyList<TerminalLine> GetLinesToEnd(long from);

    /// <summary>Renders the screen as text (trailing blanks and the blank rows at the bottom removed).</summary>
    /// <returns>The text, lines separated by <c>\n</c>.</returns>
    string RenderScreenText();
}
