using System.Text;

namespace Pillion.CommandRunner.Terminal;

/// <summary>
/// The thread-safe terminal of one session or background process: decodes the pseudo-console's UTF-8 output, runs it
/// through the <see cref="VtParser"/> into the <see cref="ScreenBuffer"/>, and exposes the result as an
/// <see cref="ITerminalBuffer"/>. Every public member takes the buffer lock.
/// </summary>
internal sealed class TerminalScreen : ITerminalBuffer
{
    private readonly object _gate = new();
    private readonly ScreenBuffer _buffer;
    private readonly VtParser _parser;
    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();
    private readonly TimeProvider _time;
    private char[] _chars = new char[4096];
    private long _generation;
    private DateTimeOffset? _lastChangedAt;

    /// <summary>Initializes a new instance of the <see cref="TerminalScreen"/> class.</summary>
    /// <param name="columns">The screen width.</param>
    /// <param name="rows">The screen height.</param>
    /// <param name="scrollbackLimit">The number of scrollback lines kept.</param>
    /// <param name="respond">Writes a reply to the application for terminal queries; may be <see langword="null"/>.</param>
    /// <param name="time">The clock for <see cref="LastChangedAt"/>.</param>
    public TerminalScreen(int columns, int rows, int scrollbackLimit, Action<string>? respond = null, TimeProvider? time = null)
    {
        _buffer = new ScreenBuffer(columns, rows, scrollbackLimit);
        _parser = new VtParser(_buffer, respond);
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public int Columns
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Columns;
            }
        }
    }

    /// <inheritdoc/>
    public int Rows
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Rows;
            }
        }
    }

    /// <inheritdoc/>
    public int ScrollbackLimit => _buffer.ScrollbackLimit;

    /// <inheritdoc/>
    public long Generation => Interlocked.Read(ref _generation);

    /// <inheritdoc/>
    public long FirstRowIndex
    {
        get
        {
            lock (_gate)
            {
                return _buffer.ScrolledLineCount;
            }
        }
    }

    /// <inheritdoc/>
    public long EndLineIndex
    {
        get
        {
            lock (_gate)
            {
                return _buffer.EndLineIndex;
            }
        }
    }

    /// <inheritdoc/>
    public long OldestLineIndex
    {
        get
        {
            lock (_gate)
            {
                return _buffer.OldestLineIndex;
            }
        }
    }

    /// <inheritdoc/>
    public DateTimeOffset? LastChangedAt
    {
        get
        {
            lock (_gate)
            {
                return _lastChangedAt;
            }
        }
    }

    /// <inheritdoc/>
    public string? Title
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Title;
            }
        }
    }

    /// <summary>Gets the number of scrollback lines currently held.</summary>
    public int ScrollbackCount
    {
        get
        {
            lock (_gate)
            {
                return _buffer.ScrollbackCount;
            }
        }
    }

    /// <summary>Feeds raw pseudo-console output.</summary>
    /// <param name="bytes">UTF-8 bytes; a character or an escape sequence may span calls.</param>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            var needed = _decoder.GetCharCount(bytes, flush: false);
            if (needed > _chars.Length)
            {
                _chars = new char[Math.Max(needed, _chars.Length * 2)];
            }

            var count = _decoder.GetChars(bytes, _chars, flush: false);
            _parser.Feed(_chars.AsSpan(0, count));
            MarkChanged();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Feeds already decoded text (tests and recorded fixtures).</summary>
    /// <param name="text">The text.</param>
    public void Write(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            _parser.Feed(text);
            MarkChanged();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the screen into the scrollback and clears it, so the next output starts at the returned history index.</summary>
    /// <returns>The history index of the next line of output.</returns>
    public long BeginRun()
    {
        long mark;
        lock (_gate)
        {
            mark = _buffer.FlushToScrollback();
            MarkChanged();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return mark;
    }

    /// <summary>Changes the screen size.</summary>
    /// <param name="columns">The new width.</param>
    /// <param name="rows">The new height.</param>
    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            _buffer.Resize(columns, rows);
            MarkChanged();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Describes the cursor line for prompt detection: whether the cursor sits after text on its line with no newline following.</summary>
    /// <returns>The cursor column, and the text of the cursor row.</returns>
    public (int CursorColumn, string CursorLine, bool AlternateScreen) GetPromptState()
    {
        lock (_gate)
        {
            return (_buffer.CursorColumn, _buffer.GetCursorLineText(), _buffer.AlternateScreen);
        }
    }

    /// <inheritdoc/>
    public TerminalSnapshot Snapshot()
    {
        lock (_gate)
        {
            var rows = new TerminalLine[_buffer.Rows];
            for (var row = 0; row < rows.Length; row++)
            {
                rows[row] = _buffer.GetRow(row);
            }

            return new TerminalSnapshot(
                _generation,
                _buffer.Columns,
                rows,
                _buffer.CursorRow,
                _buffer.CursorColumn,
                _buffer.CursorVisible,
                _buffer.ScrolledLineCount,
                _buffer.OldestLineIndex,
                _buffer.Title,
                _buffer.AlternateScreen);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<TerminalLine> GetLines(long from, long toExclusive)
    {
        lock (_gate)
        {
            return _buffer.GetLines(from, toExclusive);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<TerminalLine> GetLinesToEnd(long from)
    {
        lock (_gate)
        {
            return _buffer.GetLinesToEnd(from);
        }
    }

    /// <inheritdoc/>
    public string RenderScreenText()
    {
        lock (_gate)
        {
            return _buffer.RenderScreenText();
        }
    }

    private void MarkChanged()
    {
        Interlocked.Increment(ref _generation);
        _lastChangedAt = _time.GetUtcNow();
    }
}
