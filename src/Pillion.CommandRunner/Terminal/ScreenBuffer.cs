using System.Text;

namespace Pillion.CommandRunner.Terminal;

/// <summary>
/// The screen of a terminal: a grid of cells with a cursor, a scroll region, an alternate screen and a bounded
/// scrollback. The <see cref="VtParser"/> drives it; <see cref="TerminalBuffer"/> wraps both behind a lock. Not
/// thread-safe on its own.
/// </summary>
/// <remarks>
/// <para>
/// Lines are addressed two ways. Rows are 0-based positions on the current screen. <b>History indices</b> are absolute:
/// every line that ever scrolled off the top of the main screen got the next index, and the screen's rows follow them
/// (<see cref="ScrolledLineCount"/> is the index of the first screen row), so a caller can remember where a command's
/// output started and read everything after it even after the screen scrolled, as long as the scrollback still holds it.
/// </para>
/// <para>
/// Memory is bounded by the screen size and <see cref="ScrollbackLimit"/>: a line that leaves the scrollback is gone.
/// </para>
/// </remarks>
internal sealed class ScreenBuffer
{
    /// <summary>The widest screen accepted (ConPTY's COORD is a short).</summary>
    public const int MaxColumns = 1000;

    /// <summary>The tallest screen accepted.</summary>
    public const int MaxRows = 500;

    private readonly Queue<TerminalLine> _scrollback = new();
    private Cell[][] _rows;
    private Cell[][]? _savedMainRows;
    private int _scrollTop;
    private int _scrollBottom;
    private bool _wrapPending;
    private (int Row, int Column, CellAttributes Attributes) _savedCursor;
    private (int Row, int Column, CellAttributes Attributes) _savedAltCursor;

    /// <summary>Initializes a new instance of the <see cref="ScreenBuffer"/> class.</summary>
    /// <param name="columns">The width in cells (1 to <see cref="MaxColumns"/>).</param>
    /// <param name="rows">The height in rows (1 to <see cref="MaxRows"/>).</param>
    /// <param name="scrollbackLimit">The number of lines kept after they scroll off the main screen (0 or more).</param>
    public ScreenBuffer(int columns, int rows, int scrollbackLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, MaxColumns);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, MaxRows);
        ArgumentOutOfRangeException.ThrowIfNegative(scrollbackLimit);

        Columns = columns;
        Rows = rows;
        ScrollbackLimit = scrollbackLimit;
        _rows = NewGrid(columns, rows);
        _scrollBottom = rows - 1;
    }

    /// <summary>Gets the width in cells.</summary>
    public int Columns { get; private set; }

    /// <summary>Gets the height in rows.</summary>
    public int Rows { get; private set; }

    /// <summary>Gets the number of lines kept after they scroll off the main screen.</summary>
    public int ScrollbackLimit { get; }

    /// <summary>Gets the cursor row (0-based).</summary>
    public int CursorRow { get; private set; }

    /// <summary>Gets the cursor column (0-based). May equal <see cref="Columns"/> - 1 with a wrap pending.</summary>
    public int CursorColumn { get; private set; }

    /// <summary>Gets or sets the attributes applied to the next printed cells.</summary>
    public CellAttributes CurrentAttributes { get; set; }

    /// <summary>Gets or sets a value indicating whether the cursor is shown (DECTCEM).</summary>
    public bool CursorVisible { get; set; } = true;

    /// <summary>Gets a value indicating whether the alternate screen is active (full-screen programs; no scrollback).</summary>
    public bool AlternateScreen { get; private set; }

    /// <summary>Gets or sets the window title set by OSC 0 or 2.</summary>
    public string? Title { get; set; }

    /// <summary>Gets the number of lines that ever scrolled off the top of the main screen; the history index of the first screen row.</summary>
    public long ScrolledLineCount { get; private set; }

    /// <summary>Gets the number of scrolled-off lines still held (at most <see cref="ScrollbackLimit"/>).</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Gets the history index of the oldest line still available.</summary>
    public long OldestLineIndex => ScrolledLineCount - _scrollback.Count;

    /// <summary>Gets the history index one past the last screen row.</summary>
    public long EndLineIndex => ScrolledLineCount + Rows;

    /// <summary>Gets the row of the last cell that is not blank, or -1 for an empty screen.</summary>
    public int LastUsedRow
    {
        get
        {
            for (var row = Rows - 1; row >= 0; row--)
            {
                if (!IsBlank(_rows[row]))
                {
                    return row;
                }
            }

            return -1;
        }
    }

    // ---- printing and C0 controls ----

    /// <summary>Prints one character at the cursor with the current attributes, wrapping at the right margin.</summary>
    /// <param name="ch">The character; controls are ignored here (the parser handles them).</param>
    public void Print(char ch)
    {
        if (_wrapPending)
        {
            _wrapPending = false;
            CursorColumn = 0;
            LineFeed();
        }

        _rows[CursorRow][CursorColumn] = new Cell(ch, CurrentAttributes);
        if (CursorColumn == Columns - 1)
        {
            _wrapPending = true;
        }
        else
        {
            CursorColumn++;
        }
    }

    /// <summary>Moves the cursor down one row, scrolling the region when it is on the region's last row (LF, VT, FF, IND).</summary>
    public void LineFeed()
    {
        _wrapPending = false;
        if (CursorRow == _scrollBottom)
        {
            ScrollUp(1);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }
    }

    /// <summary>Moves the cursor to column 0 (CR).</summary>
    public void CarriageReturn()
    {
        _wrapPending = false;
        CursorColumn = 0;
    }

    /// <summary>CR then LF (NEL).</summary>
    public void NextLine()
    {
        CarriageReturn();
        LineFeed();
    }

    /// <summary>Moves the cursor up one row, scrolling the region down when it is on the region's first row (RI).</summary>
    public void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorRow == _scrollTop)
        {
            ScrollDown(1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }
    }

    /// <summary>Moves the cursor left one cell without erasing (BS).</summary>
    public void Backspace()
    {
        _wrapPending = false;
        if (CursorColumn > 0)
        {
            CursorColumn--;
        }
    }

    /// <summary>Moves the cursor to the next tab stop (every eight columns; HT).</summary>
    public void Tab()
    {
        _wrapPending = false;
        var next = ((CursorColumn / 8) + 1) * 8;
        CursorColumn = Math.Min(next, Columns - 1);
    }

    // ---- cursor movement ----

    /// <summary>Moves the cursor up (CUU), stopping at the top of the scroll region when it starts inside it.</summary>
    /// <param name="count">Rows; at least 1.</param>
    public void CursorUp(int count)
    {
        _wrapPending = false;
        var limit = CursorRow >= _scrollTop ? _scrollTop : 0;
        CursorRow = Math.Max(limit, CursorRow - Math.Max(1, count));
    }

    /// <summary>Moves the cursor down (CUD), stopping at the bottom of the scroll region when it starts inside it.</summary>
    /// <param name="count">Rows; at least 1.</param>
    public void CursorDown(int count)
    {
        _wrapPending = false;
        var limit = CursorRow <= _scrollBottom ? _scrollBottom : Rows - 1;
        CursorRow = Math.Min(limit, CursorRow + Math.Max(1, count));
    }

    /// <summary>Moves the cursor right (CUF).</summary>
    /// <param name="count">Cells; at least 1.</param>
    public void CursorForward(int count)
    {
        _wrapPending = false;
        CursorColumn = Math.Min(Columns - 1, CursorColumn + Math.Max(1, count));
    }

    /// <summary>Moves the cursor left (CUB).</summary>
    /// <param name="count">Cells; at least 1.</param>
    public void CursorBack(int count)
    {
        _wrapPending = false;
        CursorColumn = Math.Max(0, CursorColumn - Math.Max(1, count));
    }

    /// <summary>Moves the cursor to a row and column (CUP, HVP), both 0-based and clamped to the screen.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column.</param>
    public void SetCursorPosition(int row, int column)
    {
        _wrapPending = false;
        CursorRow = Math.Clamp(row, 0, Rows - 1);
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
    }

    /// <summary>Moves the cursor to a column on the current row (CHA), 0-based and clamped.</summary>
    /// <param name="column">The column.</param>
    public void SetCursorColumn(int column) => SetCursorPosition(CursorRow, column);

    /// <summary>Moves the cursor to a row in the current column (VPA), 0-based and clamped.</summary>
    /// <param name="row">The row.</param>
    public void SetCursorRow(int row) => SetCursorPosition(row, CursorColumn);

    /// <summary>Saves the cursor position and attributes (DECSC, SCOSC).</summary>
    public void SaveCursor()
    {
        if (AlternateScreen)
        {
            _savedAltCursor = (CursorRow, CursorColumn, CurrentAttributes);
        }
        else
        {
            _savedCursor = (CursorRow, CursorColumn, CurrentAttributes);
        }
    }

    /// <summary>Restores the cursor position and attributes (DECRC, SCORC).</summary>
    public void RestoreCursor()
    {
        var saved = AlternateScreen ? _savedAltCursor : _savedCursor;
        SetCursorPosition(saved.Row, saved.Column);
        CurrentAttributes = saved.Attributes;
    }

    // ---- erasing ----

    /// <summary>Erases part of the screen (ED). Erased cells get the current background.</summary>
    /// <param name="mode">0: cursor to end; 1: start to cursor; 2: whole screen; 3: whole screen (the scrollback is Pillion's record and is kept).</param>
    public void EraseInDisplay(int mode)
    {
        _wrapPending = false;
        if (mode is 2 or 3 && !AlternateScreen)
        {
            // A full erase on the main screen (cls, a logger starting over) keeps the erased lines as history rather than
            // losing them: the scrollback is Pillion's record of the run, not the application's.
            var last = LastUsedRow;
            for (var row = 0; row <= last; row++)
            {
                PushScrollback(RenderRow(_rows[row]));
                ScrolledLineCount++;
            }
        }

        switch (mode)
        {
            case 0:
                EraseCells(CursorRow, CursorColumn, Columns - CursorColumn);
                for (var row = CursorRow + 1; row < Rows; row++)
                {
                    EraseCells(row, 0, Columns);
                }

                break;
            case 1:
                for (var row = 0; row < CursorRow; row++)
                {
                    EraseCells(row, 0, Columns);
                }

                EraseCells(CursorRow, 0, CursorColumn + 1);
                break;
            case 2:
            case 3:
                for (var row = 0; row < Rows; row++)
                {
                    EraseCells(row, 0, Columns);
                }

                break;
        }
    }

    /// <summary>Erases part of the cursor line (EL).</summary>
    /// <param name="mode">0: cursor to end; 1: start to cursor; 2: whole line.</param>
    public void EraseInLine(int mode)
    {
        _wrapPending = false;
        switch (mode)
        {
            case 0:
                EraseCells(CursorRow, CursorColumn, Columns - CursorColumn);
                break;
            case 1:
                EraseCells(CursorRow, 0, CursorColumn + 1);
                break;
            case 2:
                EraseCells(CursorRow, 0, Columns);
                break;
        }
    }

    /// <summary>Erases cells from the cursor rightwards without moving it (ECH).</summary>
    /// <param name="count">Cells; at least 1.</param>
    public void EraseCharacters(int count)
    {
        _wrapPending = false;
        EraseCells(CursorRow, CursorColumn, Math.Min(Math.Max(1, count), Columns - CursorColumn));
    }

    // ---- insertion and deletion ----

    /// <summary>Inserts blank cells at the cursor, pushing the rest of the line right (ICH).</summary>
    /// <param name="count">Cells; at least 1.</param>
    public void InsertCharacters(int count)
    {
        _wrapPending = false;
        count = Math.Min(Math.Max(1, count), Columns - CursorColumn);
        var row = _rows[CursorRow];
        Array.Copy(row, CursorColumn, row, CursorColumn + count, Columns - CursorColumn - count);
        EraseCells(CursorRow, CursorColumn, count);
    }

    /// <summary>Deletes cells at the cursor, pulling the rest of the line left (DCH).</summary>
    /// <param name="count">Cells; at least 1.</param>
    public void DeleteCharacters(int count)
    {
        _wrapPending = false;
        count = Math.Min(Math.Max(1, count), Columns - CursorColumn);
        var row = _rows[CursorRow];
        Array.Copy(row, CursorColumn + count, row, CursorColumn, Columns - CursorColumn - count);
        EraseCells(CursorRow, Columns - count, count);
    }

    /// <summary>Inserts blank lines at the cursor row, pushing the rows below down within the scroll region (IL).</summary>
    /// <param name="count">Lines; at least 1.</param>
    public void InsertLines(int count)
    {
        _wrapPending = false;
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
        {
            return;
        }

        ScrollRegionDown(CursorRow, _scrollBottom, Math.Max(1, count));
        CursorColumn = 0;
    }

    /// <summary>Deletes lines at the cursor row, pulling the rows below up within the scroll region (DL).</summary>
    /// <param name="count">Lines; at least 1.</param>
    public void DeleteLines(int count)
    {
        _wrapPending = false;
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
        {
            return;
        }

        ScrollRegionUp(CursorRow, _scrollBottom, Math.Max(1, count), toScrollback: false);
        CursorColumn = 0;
    }

    // ---- scrolling ----

    /// <summary>Sets the scroll region (DECSTBM), 0-based inclusive; invalid values reset it to the whole screen. Homes the cursor.</summary>
    /// <param name="top">The first row.</param>
    /// <param name="bottom">The last row.</param>
    public void SetScrollRegion(int top, int bottom)
    {
        if (top < 0 || bottom >= Rows || top >= bottom)
        {
            top = 0;
            bottom = Rows - 1;
        }

        _scrollTop = top;
        _scrollBottom = bottom;
        SetCursorPosition(0, 0);
    }

    /// <summary>Scrolls the region up (SU): the top lines leave (into the scrollback when the region starts at row 0 on the main screen).</summary>
    /// <param name="count">Lines; at least 1.</param>
    public void ScrollUp(int count) => ScrollRegionUp(_scrollTop, _scrollBottom, Math.Max(1, count), toScrollback: _scrollTop == 0 && !AlternateScreen);

    /// <summary>Scrolls the region down (SD): blank lines enter at the top and the bottom lines are lost.</summary>
    /// <param name="count">Lines; at least 1.</param>
    public void ScrollDown(int count) => ScrollRegionDown(_scrollTop, _scrollBottom, Math.Max(1, count));

    // ---- screens ----

    /// <summary>Switches between the main and the alternate screen (DECSET/DECRST 47, 1047, 1049). The alternate screen starts blank and keeps no scrollback.</summary>
    /// <param name="alternate"><see langword="true"/> for the alternate screen.</param>
    public void SetAlternateScreen(bool alternate)
    {
        if (alternate == AlternateScreen)
        {
            return;
        }

        _wrapPending = false;
        if (alternate)
        {
            _savedMainRows = _rows;
            _savedCursor = (CursorRow, CursorColumn, CurrentAttributes);
            _rows = NewGrid(Columns, Rows);
            AlternateScreen = true;
            SetCursorPosition(0, 0);
        }
        else
        {
            _rows = _savedMainRows ?? NewGrid(Columns, Rows);
            _savedMainRows = null;
            AlternateScreen = false;
            SetCursorPosition(_savedCursor.Row, _savedCursor.Column);
            CurrentAttributes = _savedCursor.Attributes;
        }

        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    /// <summary>Resets the terminal state (RIS): main screen, cleared, default attributes, whole-screen region. The scrollback is kept.</summary>
    public void Reset()
    {
        SetAlternateScreen(false);
        CurrentAttributes = CellAttributes.Default;
        CursorVisible = true;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _wrapPending = false;
        for (var row = 0; row < Rows; row++)
        {
            Array.Clear(_rows[row]);
        }

        SetCursorPosition(0, 0);
    }

    /// <summary>
    /// Moves everything on the main screen up to its last used row into the scrollback, clears the screen and homes the
    /// cursor, so the output of the next command starts on a clean screen and the history index returned is its first line.
    /// </summary>
    /// <returns>The history index the next output will start at.</returns>
    public long FlushToScrollback()
    {
        SetAlternateScreen(false);
        var last = LastUsedRow;
        for (var row = 0; row <= last; row++)
        {
            PushScrollback(RenderRow(_rows[row]));
            ScrolledLineCount++;
        }

        for (var row = 0; row < Rows; row++)
        {
            Array.Clear(_rows[row]);
        }

        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _wrapPending = false;
        SetCursorPosition(0, 0);
        return ScrolledLineCount;
    }

    /// <summary>Appends a line of Pillion's own (a command header) to the history above the screen, which must be clear.</summary>
    /// <param name="line">The line.</param>
    /// <returns>Its history index.</returns>
    public long AppendHistoryLine(TerminalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        PushScrollback(line);
        return ScrolledLineCount++;
    }

    /// <summary>
    /// Changes the screen size. Content stays anchored at the top left; rows that no longer fit above the cursor go to the
    /// scrollback (main screen only), and the cursor and the scroll region are clamped.
    /// </summary>
    /// <param name="columns">The new width (1 to <see cref="MaxColumns"/>).</param>
    /// <param name="rows">The new height (1 to <see cref="MaxRows"/>).</param>
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, MaxColumns);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, MaxRows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        _wrapPending = false;
        _rows = ResizeGrid(_rows, columns, rows, pushToScrollback: !AlternateScreen);
        if (_savedMainRows is not null)
        {
            _savedMainRows = ResizeGrid(_savedMainRows, columns, rows, pushToScrollback: true);
        }

        Columns = columns;
        Rows = rows;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        CursorRow = Math.Clamp(CursorRow, 0, rows - 1);
        CursorColumn = Math.Clamp(CursorColumn, 0, columns - 1);
    }

    // ---- reading ----

    /// <summary>Renders one screen row.</summary>
    /// <param name="row">The row, 0-based.</param>
    /// <returns>The line.</returns>
    public TerminalLine GetRow(int row) => RenderRow(_rows[row]);

    /// <summary>Renders the lines in a history range, oldest first, skipping lines that have left the scrollback.</summary>
    /// <param name="from">The first history index wanted.</param>
    /// <param name="to">One past the last history index wanted; clamped to <see cref="EndLineIndex"/>.</param>
    /// <returns>The lines, possibly fewer than asked for.</returns>
    public List<TerminalLine> GetLines(long from, long to)
    {
        var result = new List<TerminalLine>();
        from = Math.Max(from, OldestLineIndex);
        to = Math.Min(to, EndLineIndex);
        if (from >= to)
        {
            return result;
        }

        if (from < ScrolledLineCount)
        {
            var skip = (int)(from - OldestLineIndex);
            var take = (int)(Math.Min(to, ScrolledLineCount) - from);
            foreach (var line in _scrollback)
            {
                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                if (take-- <= 0)
                {
                    break;
                }

                result.Add(line);
            }
        }

        for (var index = Math.Max(from, ScrolledLineCount); index < to; index++)
        {
            result.Add(RenderRow(_rows[(int)(index - ScrolledLineCount)]));
        }

        return result;
    }

    /// <summary>Renders the lines from a history index to the last used screen row (blank rows at the bottom dropped).</summary>
    /// <param name="from">The first history index wanted.</param>
    /// <returns>The lines.</returns>
    public List<TerminalLine> GetLinesToEnd(long from) => GetLines(from, ScrolledLineCount + LastUsedRow + 1);

    /// <summary>Renders the whole screen as text, one line per row, trailing blanks and blank rows at the bottom removed.</summary>
    /// <returns>The text.</returns>
    public string RenderScreenText()
    {
        var builder = new StringBuilder();
        var last = LastUsedRow;
        for (var row = 0; row <= last; row++)
        {
            if (row > 0)
            {
                builder.Append('\n');
            }

            AppendRowText(builder, _rows[row]);
        }

        return builder.ToString();
    }

    /// <summary>Gets the text of the cursor row up to the cursor (what sits before a waiting prompt).</summary>
    /// <returns>The text, trailing blanks removed.</returns>
    public string GetCursorLineText()
    {
        var builder = new StringBuilder();
        AppendRowText(builder, _rows[CursorRow]);
        return builder.ToString();
    }

    // ---- internals ----

    private static Cell[][] NewGrid(int columns, int rows)
    {
        var grid = new Cell[rows][];
        for (var row = 0; row < rows; row++)
        {
            grid[row] = new Cell[columns];
        }

        return grid;
    }

    private static bool IsBlank(Cell[] row)
    {
        foreach (var cell in row)
        {
            if (cell.Character is not ('\0' or ' ') || !cell.Attributes.Background.IsDefault)
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendRowText(StringBuilder builder, Cell[] row)
    {
        var end = row.Length;
        while (end > 0 && row[end - 1].Character is '\0' or ' ')
        {
            end--;
        }

        for (var column = 0; column < end; column++)
        {
            var ch = row[column].Character;
            builder.Append(ch == '\0' ? ' ' : ch);
        }
    }

    private static TerminalLine RenderRow(Cell[] row)
    {
        var end = row.Length;
        while (end > 0 && row[end - 1].Character is '\0' or ' ')
        {
            end--;
        }

        if (end == 0)
        {
            return TerminalLine.Empty;
        }

        var text = new char[end];
        List<TerminalSpan>? spans = null;
        var spanStart = 0;
        var spanAttributes = row[0].Attributes;
        var allDefault = true;
        for (var column = 0; column < end; column++)
        {
            var cell = row[column];
            text[column] = cell.Character == '\0' ? ' ' : cell.Character;
            if (!cell.Attributes.IsDefault)
            {
                allDefault = false;
            }

            if (cell.Attributes != spanAttributes)
            {
                (spans ??= []).Add(new TerminalSpan(spanStart, column - spanStart, spanAttributes));
                spanStart = column;
                spanAttributes = cell.Attributes;
            }
        }

        if (allDefault)
        {
            return new TerminalLine(new string(text), []);
        }

        (spans ??= []).Add(new TerminalSpan(spanStart, end - spanStart, spanAttributes));
        return new TerminalLine(new string(text), spans);
    }

    private void EraseCells(int row, int start, int count)
    {
        var cells = _rows[row];
        var blank = new Cell('\0', new CellAttributes(TerminalColor.Default, CurrentAttributes.Background, CellStyle.None));
        for (var column = start; column < start + count && column < cells.Length; column++)
        {
            cells[column] = blank;
        }
    }

    private void ScrollRegionUp(int top, int bottom, int count, bool toScrollback)
    {
        count = Math.Min(count, bottom - top + 1);
        for (var i = 0; i < count; i++)
        {
            var leaving = _rows[top + i];
            if (toScrollback)
            {
                PushScrollback(RenderRow(leaving));
                ScrolledLineCount++;
            }
        }

        // Rotate the row arrays rather than copying cells: the leaving rows become the new blank rows at the bottom.
        var recycled = new Cell[count][];
        Array.Copy(_rows, top, recycled, 0, count);
        Array.Copy(_rows, top + count, _rows, top, bottom - top + 1 - count);
        for (var i = 0; i < count; i++)
        {
            Array.Clear(recycled[i]);
            _rows[bottom - count + 1 + i] = recycled[i];
        }
    }

    private void ScrollRegionDown(int top, int bottom, int count)
    {
        count = Math.Min(count, bottom - top + 1);
        var recycled = new Cell[count][];
        Array.Copy(_rows, bottom - count + 1, recycled, 0, count);
        Array.Copy(_rows, top, _rows, top + count, bottom - top + 1 - count);
        for (var i = 0; i < count; i++)
        {
            Array.Clear(recycled[i]);
            _rows[top + i] = recycled[i];
        }
    }

    private void PushScrollback(TerminalLine line)
    {
        if (ScrollbackLimit == 0)
        {
            return;
        }

        _scrollback.Enqueue(line);
        while (_scrollback.Count > ScrollbackLimit)
        {
            _scrollback.Dequeue();
        }
    }

    private Cell[][] ResizeGrid(Cell[][] grid, int columns, int rows, bool pushToScrollback)
    {
        var oldRows = grid.Length;
        var oldColumns = oldRows > 0 ? grid[0].Length : 0;

        // Rows above the cursor that no longer fit leave through the top.
        var drop = 0;
        if (rows < oldRows)
        {
            var lastUsed = -1;
            for (var row = oldRows - 1; row >= 0; row--)
            {
                if (!IsBlank(grid[row]))
                {
                    lastUsed = row;
                    break;
                }
            }

            var keepThrough = Math.Max(lastUsed, CursorRow);
            drop = Math.Max(0, keepThrough + 1 - rows);
            if (pushToScrollback)
            {
                for (var row = 0; row < drop; row++)
                {
                    PushScrollback(RenderRow(grid[row]));
                    ScrolledLineCount++;
                }
            }

            if (ReferenceEquals(grid, _rows))
            {
                CursorRow -= drop;
            }
        }

        var result = NewGrid(columns, rows);
        for (var row = 0; row < rows && row + drop < oldRows; row++)
        {
            Array.Copy(grid[row + drop], result[row], Math.Min(columns, oldColumns));
        }

        return result;
    }

    /// <summary>One cell: a UTF-16 code unit ('\0' for a blank) and its attributes.</summary>
    private readonly record struct Cell(char Character, CellAttributes Attributes);
}
