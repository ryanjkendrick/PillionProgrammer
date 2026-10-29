using System.Text;

namespace Pillion.CommandRunner.Terminal;

/// <summary>
/// A VT100/xterm escape-sequence parser that drives a <see cref="ScreenBuffer"/>. A bounded state machine after the
/// VT500 parser model: printable text goes to the screen; CSI, ESC and OSC sequences it knows are applied; everything
/// else is consumed and dropped, so no escape sequence or control byte ever reaches the text of a line.
/// </summary>
/// <remarks>
/// Handled: cursor movement (CUU, CUD, CUF, CUB, CNL, CPL, CHA, VPA, CUP, HVP), erase in display and line (ED, EL, ECH),
/// insert and delete (ICH, DCH, IL, DL), scroll region and scrolling (DECSTBM, SU, SD, IND, RI, NEL), SGR including 256 and
/// 24-bit colours, save and restore cursor (DECSC, DECRC, SCOSC, SCORC), the C0 controls CR, LF, VT, FF, BS, HT, the OSC
/// title (0 and 2), the alternate screen (47, 1047, 1049), cursor visibility (25), and RIS. Bracketed paste (2004), the
/// keypad and cursor-key modes, charset designations, DCS, SOS, PM and APC strings, device attribute queries and every
/// other sequence are consumed without touching the buffer. Cursor position (DSR 6) and primary device attribute (DA)
/// requests are answered through the responder when one is given.
/// </remarks>
internal sealed class VtParser
{
    private const int MaxParameters = 32;
    private const int MaxStringLength = 4096;

    private readonly ScreenBuffer _screen;
    private readonly Action<string>? _respond;
    private readonly int[] _parameters = new int[MaxParameters];
    private readonly bool[] _subParameter = new bool[MaxParameters];
    private readonly StringBuilder _oscText = new();
    private State _state = State.Ground;
    private int _parameterCount;
    private char _privateMarker;
    private char _intermediate;
    private int _intermediateCount;

    /// <summary>Initializes a new instance of the <see cref="VtParser"/> class.</summary>
    /// <param name="screen">The screen to drive.</param>
    /// <param name="respond">Writes a reply to the application for the queries the parser answers; <see langword="null"/> to answer none.</param>
    public VtParser(ScreenBuffer screen, Action<string>? respond = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        _screen = screen;
        _respond = respond;
    }

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParameter,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        OscStringEscape,
        OtherString,
        OtherStringEscape,
        CharsetDesignation,
    }

    /// <summary>Feeds decoded text to the parser.</summary>
    /// <param name="text">The characters, in stream order; a sequence may span calls.</param>
    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var ch in text)
        {
            Feed(ch);
        }
    }

    /// <summary>Feeds one character.</summary>
    /// <param name="ch">The character.</param>
    public void Feed(char ch)
    {
        // CAN and SUB abort any sequence; ESC starts a new one from any state except inside strings, where it may be the ST.
        if (ch is '\u0018' or '\u001A')
        {
            _state = State.Ground;
            return;
        }

        switch (_state)
        {
            case State.Ground:
                if (ch < ' ')
                {
                    ExecuteControl(ch);
                }
                else if (ch is not ('\u007F' or (>= '\u0080' and <= '\u009F')))
                {
                    _screen.Print(ch);
                }

                break;

            case State.Escape:
                HandleEscape(ch);
                break;

            case State.EscapeIntermediate:
                if (ch < ' ')
                {
                    ExecuteControl(ch);
                }
                else if (ch >= '0')
                {
                    // Final byte of a two-intermediate escape (none we act on besides charset designations, handled before).
                    _state = State.Ground;
                }

                break;

            case State.CharsetDesignation:
                if (ch >= ' ')
                {
                    _state = State.Ground;
                }

                break;

            case State.CsiEntry:
            case State.CsiParameter:
            case State.CsiIntermediate:
            case State.CsiIgnore:
                HandleCsi(ch);
                break;

            case State.OscString:
                if (ch == '\u0007')
                {
                    FinishOsc();
                }
                else if (ch == '\u001B')
                {
                    _state = State.OscStringEscape;
                }
                else if (ch >= ' ' && _oscText.Length < MaxStringLength)
                {
                    _oscText.Append(ch);
                }

                break;

            case State.OscStringEscape:
                if (ch == '\\')
                {
                    FinishOsc();
                }
                else
                {
                    // Not ST: the OSC was abandoned and a new escape begins.
                    _oscText.Clear();
                    _state = State.Escape;
                    HandleEscape(ch);
                }

                break;

            case State.OtherString:
                if (ch == '\u001B')
                {
                    _state = State.OtherStringEscape;
                }
                else if (ch == '\u0007')
                {
                    _state = State.Ground;
                }

                break;

            case State.OtherStringEscape:
                if (ch == '\\')
                {
                    _state = State.Ground;
                }
                else
                {
                    _state = State.Escape;
                    HandleEscape(ch);
                }

                break;
        }
    }

    private void ExecuteControl(char ch)
    {
        switch (ch)
        {
            case '\n':
            case '\u000B':
            case '\u000C':
                _screen.LineFeed();
                break;
            case '\r':
                _screen.CarriageReturn();
                break;
            case '\b':
                _screen.Backspace();
                break;
            case '\t':
                _screen.Tab();
                break;
            case '\u001B':
                BeginEscape();
                break;
            default:
                // BEL, NUL, SO, SI, ENQ and the rest: nothing to show.
                break;
        }
    }

    private void BeginEscape()
    {
        _state = State.Escape;
        _parameterCount = 0;
        _parameters[0] = 0;
        _subParameter[0] = false;
        _privateMarker = '\0';
        _intermediate = '\0';
        _intermediateCount = 0;
    }

    private void HandleEscape(char ch)
    {
        if (ch < ' ')
        {
            ExecuteControl(ch);
            return;
        }

        switch (ch)
        {
            case '[':
                _state = State.CsiEntry;
                break;
            case ']':
                _oscText.Clear();
                _state = State.OscString;
                break;
            case 'P':
            case 'X':
            case '^':
            case '_':
                _state = State.OtherString;
                break;
            case '(':
            case ')':
            case '*':
            case '+':
            case '-':
            case '.':
            case '/':
                _state = State.CharsetDesignation;
                break;
            case '7':
                _screen.SaveCursor();
                _state = State.Ground;
                break;
            case '8':
                _screen.RestoreCursor();
                _state = State.Ground;
                break;
            case 'D':
                _screen.LineFeed();
                _state = State.Ground;
                break;
            case 'E':
                _screen.NextLine();
                _state = State.Ground;
                break;
            case 'M':
                _screen.ReverseIndex();
                _state = State.Ground;
                break;
            case 'c':
                _screen.Reset();
                _state = State.Ground;
                break;
            case ' ':
            case '#':
            case '%':
                _state = State.EscapeIntermediate;
                break;
            default:
                // ESC =, ESC >, ESC \ (a stray ST), ESC H and the rest: nothing to show.
                _state = State.Ground;
                break;
        }
    }

    private void HandleCsi(char ch)
    {
        if (ch < ' ')
        {
            ExecuteControl(ch);
            return;
        }

        if (ch == '\u007F')
        {
            return;
        }

        if (_state == State.CsiIgnore)
        {
            if (ch >= '@' && ch <= '~')
            {
                _state = State.Ground;
            }

            return;
        }

        if (ch >= '0' && ch <= '9')
        {
            if (_state == State.CsiIntermediate)
            {
                _state = State.CsiIgnore;
                return;
            }

            _state = State.CsiParameter;
            if (_parameterCount == 0)
            {
                _parameterCount = 1;
            }

            var index = _parameterCount - 1;
            var value = _parameters[index];
            _parameters[index] = value > 100_000 ? value : (value * 10) + (ch - '0');
            return;
        }

        if (ch is ';' or ':')
        {
            if (_state == State.CsiIntermediate)
            {
                _state = State.CsiIgnore;
                return;
            }

            _state = State.CsiParameter;
            if (_parameterCount == 0)
            {
                _parameterCount = 1;
            }

            if (_parameterCount < MaxParameters)
            {
                _parameters[_parameterCount] = 0;
                _subParameter[_parameterCount] = ch == ':';
                _parameterCount++;
            }

            return;
        }

        if (ch >= '<' && ch <= '?')
        {
            if (_state == State.CsiEntry)
            {
                _privateMarker = ch;
                _state = State.CsiParameter;
            }
            else
            {
                _state = State.CsiIgnore;
            }

            return;
        }

        if (ch >= ' ' && ch <= '/')
        {
            _intermediate = ch;
            _intermediateCount++;
            _state = State.CsiIntermediate;
            return;
        }

        // Final byte.
        _state = State.Ground;
        if (_intermediateCount > 1)
        {
            return;
        }

        Dispatch(ch);
    }

    private int Parameter(int index, int fallback)
    {
        if (index >= _parameterCount)
        {
            return fallback;
        }

        var value = _parameters[index];
        return value == 0 ? fallback : value;
    }

    private void Dispatch(char final)
    {
        if (_intermediate != '\0')
        {
            // CSI ! p (DECSTR soft reset) is the only intermediate form acted on.
            if (_intermediate == '!' && final == 'p' && _privateMarker == '\0')
            {
                _screen.CurrentAttributes = CellAttributes.Default;
                _screen.CursorVisible = true;
                _screen.SetScrollRegion(0, _screen.Rows - 1);
            }

            return;
        }

        if (_privateMarker == '?')
        {
            if (final is 'h' or 'l')
            {
                SetPrivateModes(final == 'h');
            }

            return;
        }

        if (_privateMarker != '\0')
        {
            return;
        }

        switch (final)
        {
            case 'A':
                _screen.CursorUp(Parameter(0, 1));
                break;
            case 'B':
            case 'e':
                _screen.CursorDown(Parameter(0, 1));
                break;
            case 'C':
            case 'a':
                _screen.CursorForward(Parameter(0, 1));
                break;
            case 'D':
                _screen.CursorBack(Parameter(0, 1));
                break;
            case 'E':
                _screen.CursorDown(Parameter(0, 1));
                _screen.CarriageReturn();
                break;
            case 'F':
                _screen.CursorUp(Parameter(0, 1));
                _screen.CarriageReturn();
                break;
            case 'G':
            case '`':
                _screen.SetCursorColumn(Parameter(0, 1) - 1);
                break;
            case 'd':
                _screen.SetCursorRow(Parameter(0, 1) - 1);
                break;
            case 'H':
            case 'f':
                _screen.SetCursorPosition(Parameter(0, 1) - 1, Parameter(1, 1) - 1);
                break;
            case 'J':
                _screen.EraseInDisplay(Parameter(0, 0));
                break;
            case 'K':
                _screen.EraseInLine(Parameter(0, 0));
                break;
            case 'L':
                _screen.InsertLines(Parameter(0, 1));
                break;
            case 'M':
                _screen.DeleteLines(Parameter(0, 1));
                break;
            case 'P':
                _screen.DeleteCharacters(Parameter(0, 1));
                break;
            case '@':
                _screen.InsertCharacters(Parameter(0, 1));
                break;
            case 'S':
                _screen.ScrollUp(Parameter(0, 1));
                break;
            case 'T':
                _screen.ScrollDown(Parameter(0, 1));
                break;
            case 'X':
                _screen.EraseCharacters(Parameter(0, 1));
                break;
            case 'r':
                _screen.SetScrollRegion(Parameter(0, 1) - 1, Parameter(1, _screen.Rows) - 1);
                break;
            case 's':
                _screen.SaveCursor();
                break;
            case 'u':
                _screen.RestoreCursor();
                break;
            case 'm':
                ApplySgr();
                break;
            case 'n':
                if (Parameter(0, 0) == 6)
                {
                    _respond?.Invoke($"\u001B[{_screen.CursorRow + 1};{_screen.CursorColumn + 1}R");
                }
                else if (Parameter(0, 0) == 5)
                {
                    _respond?.Invoke("\u001B[0n");
                }

                break;
            case 'c':
                _respond?.Invoke("\u001B[?1;0c");
                break;
            case 't':
                // Window manipulation: a query (CSI 18 t) gets the screen size; everything else is ignored.
                if (Parameter(0, 0) == 18)
                {
                    _respond?.Invoke($"\u001B[8;{_screen.Rows};{_screen.Columns}t");
                }

                break;
            default:
                // h, l (ANSI modes), g (tab clear), q, and anything unknown: dropped.
                break;
        }
    }

    private void SetPrivateModes(bool set)
    {
        for (var i = 0; i < Math.Max(1, _parameterCount); i++)
        {
            switch (Parameter(i, 0))
            {
                case 25:
                    _screen.CursorVisible = set;
                    break;
                case 47:
                case 1047:
                    _screen.SetAlternateScreen(set);
                    break;
                case 1049:
                    if (set)
                    {
                        _screen.SaveCursor();
                        _screen.SetAlternateScreen(true);
                    }
                    else
                    {
                        _screen.SetAlternateScreen(false);
                        _screen.RestoreCursor();
                    }

                    break;
                default:
                    // 1 (DECCKM), 7 (DECAWM, always on here), 12, 1004, 2004 (bracketed paste), 9001 and the rest: no-ops.
                    break;
            }
        }
    }

    private void ApplySgr()
    {
        var attributes = _screen.CurrentAttributes;
        if (_parameterCount == 0)
        {
            _screen.CurrentAttributes = CellAttributes.Default;
            return;
        }

        for (var i = 0; i < _parameterCount; i++)
        {
            var code = _parameters[i];
            switch (code)
            {
                case 0:
                    attributes = CellAttributes.Default;
                    break;
                case 1:
                    attributes = attributes with { Style = attributes.Style | CellStyle.Bold };
                    break;
                case 2:
                    attributes = attributes with { Style = attributes.Style | CellStyle.Faint };
                    break;
                case 3:
                    attributes = attributes with { Style = attributes.Style | CellStyle.Italic };
                    break;
                case 4:
                    attributes = attributes with { Style = attributes.Style | CellStyle.Underline };
                    break;
                case 7:
                    attributes = attributes with { Style = attributes.Style | CellStyle.Inverse };
                    break;
                case 9:
                    attributes = attributes with { Style = attributes.Style | CellStyle.Strikethrough };
                    break;
                case 22:
                    attributes = attributes with { Style = attributes.Style & ~(CellStyle.Bold | CellStyle.Faint) };
                    break;
                case 23:
                    attributes = attributes with { Style = attributes.Style & ~CellStyle.Italic };
                    break;
                case 24:
                    attributes = attributes with { Style = attributes.Style & ~CellStyle.Underline };
                    break;
                case 27:
                    attributes = attributes with { Style = attributes.Style & ~CellStyle.Inverse };
                    break;
                case 29:
                    attributes = attributes with { Style = attributes.Style & ~CellStyle.Strikethrough };
                    break;
                case >= 30 and <= 37:
                    attributes = attributes with { Foreground = TerminalColor.Indexed(code - 30) };
                    break;
                case 38:
                    attributes = attributes with { Foreground = ReadExtendedColor(ref i) };
                    break;
                case 39:
                    attributes = attributes with { Foreground = TerminalColor.Default };
                    break;
                case >= 40 and <= 47:
                    attributes = attributes with { Background = TerminalColor.Indexed(code - 40) };
                    break;
                case 48:
                    attributes = attributes with { Background = ReadExtendedColor(ref i) };
                    break;
                case 49:
                    attributes = attributes with { Background = TerminalColor.Default };
                    break;
                case >= 90 and <= 97:
                    attributes = attributes with { Foreground = TerminalColor.Indexed(code - 90 + 8) };
                    break;
                case >= 100 and <= 107:
                    attributes = attributes with { Background = TerminalColor.Indexed(code - 100 + 8) };
                    break;
                default:
                    // 5, 6 (blink), 8 (conceal), 21, 25, 28, 51 to 65 (frames, ideogram, underline colour) and unknown codes.
                    if (code == 58 || code == 59)
                    {
                        if (code == 58)
                        {
                            _ = ReadExtendedColor(ref i);
                        }
                    }

                    break;
            }
        }

        _screen.CurrentAttributes = attributes;
    }

    /// <summary>Reads the colour after SGR 38, 48 or 58 in either the <c>;</c> or the <c>:</c> form and advances past it.</summary>
    private TerminalColor ReadExtendedColor(ref int index)
    {
        if (index + 1 >= _parameterCount)
        {
            return TerminalColor.Default;
        }

        var colon = _subParameter[index + 1];
        var kind = _parameters[index + 1];
        switch (kind)
        {
            case 5 when index + 2 < _parameterCount:
                index += 2;
                return TerminalColor.Indexed(_parameters[index]);
            case 2:
                {
                    var start = index + 2;
                    // The colon form may carry a colour-space id before the components (38:2::r:g:b).
                    if (colon && start + 3 < _parameterCount && _subParameter[start + 3])
                    {
                        start++;
                    }

                    if (start + 2 >= _parameterCount)
                    {
                        index = _parameterCount - 1;
                        return TerminalColor.Default;
                    }

                    index = start + 2;
                    return TerminalColor.Rgb(_parameters[start], _parameters[start + 1], _parameters[start + 2]);
                }

            default:
                index++;
                return TerminalColor.Default;
        }
    }

    private void FinishOsc()
    {
        _state = State.Ground;
        var text = _oscText.ToString();
        _oscText.Clear();
        var separator = text.IndexOf(';', StringComparison.Ordinal);
        if (separator < 0)
        {
            return;
        }

        var code = text[..separator];
        if (code is "0" or "2")
        {
            _screen.Title = text[(separator + 1)..];
        }

        // 1 (icon name), 4 (palette), 7 (working directory), 8 (hyperlinks), 9 and 133 (progress, shell integration), 52
        // (clipboard, never honoured), and the rest are dropped.
    }
}
