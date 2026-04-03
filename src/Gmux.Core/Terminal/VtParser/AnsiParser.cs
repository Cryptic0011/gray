using System.Text;

namespace Gmux.Core.Terminal.VtParser;

/// <summary>
/// Minimal VT100/ANSI escape sequence parser.
/// Parses raw ConPTY output and applies it to a TerminalBuffer.
/// </summary>
public class AnsiParser
{
    private readonly TerminalBuffer _buffer;
    private ParserState _state = ParserState.Ground;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _intermediates = new();
    private bool _privateMode; // '?' prefix in CSI

    // Saved cursor state for ESC 7 / ESC 8
    private int _savedCursorRow;
    private int _savedCursorCol;
    private byte _savedForeground;
    private byte _savedBackground;
    private CellAttributes _savedAttributes;

    private enum ParserState
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        OscString,
        OscStringEscape,
    }

    public AnsiParser(TerminalBuffer buffer)
    {
        _buffer = buffer;
    }

    public void ProcessChars(ReadOnlySpan<char> data)
    {
        foreach (char c in data)
            ProcessChar(c);
    }

    public void ProcessText(string text)
    {
        foreach (char c in text)
            ProcessChar(c);
    }

    private void ProcessChar(char c)
    {
        switch (_state)
        {
            case ParserState.Ground:
                if (c == '\x1b')
                {
                    _state = ParserState.Escape;
                }
                else
                {
                    _buffer.WriteChar(c);
                }
                break;

            case ParserState.Escape:
                switch (c)
                {
                    case '[':
                        _state = ParserState.CsiEntry;
                        _params.Clear();
                        _intermediates.Clear();
                        _privateMode = false;
                        break;
                    case ']':
                        _state = ParserState.OscString;
                        _params.Clear();
                        break;
                    case '(':
                    case ')':
                    case '*':
                    case '+':
                        // Character set designation - skip next char
                        _state = ParserState.EscapeIntermediate;
                        break;
                    case '7': // Save cursor
                        _savedCursorRow = _buffer.CursorRow;
                        _savedCursorCol = _buffer.CursorCol;
                        _savedForeground = _buffer.CurrentForeground;
                        _savedBackground = _buffer.CurrentBackground;
                        _savedAttributes = _buffer.CurrentAttributes;
                        _state = ParserState.Ground;
                        break;
                    case '8': // Restore cursor
                        _buffer.CursorRow = _savedCursorRow;
                        _buffer.CursorCol = _savedCursorCol;
                        _buffer.CurrentForeground = _savedForeground;
                        _buffer.CurrentBackground = _savedBackground;
                        _buffer.CurrentAttributes = _savedAttributes;
                        _state = ParserState.Ground;
                        break;
                    case 'M': // Reverse index
                        if (_buffer.CursorRow <= _buffer.ScrollTop)
                            _buffer.ScrollDown(1);
                        else
                            _buffer.CursorRow--;
                        _state = ParserState.Ground;
                        break;
                    case 'D': // Index (linefeed)
                        _buffer.LineFeed();
                        _state = ParserState.Ground;
                        break;
                    case 'E': // Next line
                        _buffer.CursorCol = 0;
                        _buffer.LineFeed();
                        _state = ParserState.Ground;
                        break;
                    case 'c': // Full reset
                        _buffer.Clear();
                        _state = ParserState.Ground;
                        break;
                    default:
                        _state = ParserState.Ground;
                        break;
                }
                break;

            case ParserState.EscapeIntermediate:
                // Consume one character after ESC ( / ) / * / +
                _state = ParserState.Ground;
                break;

            case ParserState.CsiEntry:
                if (c == '\x1b') { _state = ParserState.Escape; break; }
                if (c == '?')
                {
                    _privateMode = true;
                    _state = ParserState.CsiParam;
                }
                else if (c is >= '0' and <= '9' or ';')
                {
                    _params.Append(c);
                    _state = ParserState.CsiParam;
                }
                else if (c is >= ' ' and <= '/')
                {
                    _intermediates.Append(c);
                    _state = ParserState.CsiIntermediate;
                }
                else
                {
                    ExecuteCsi(c);
                    _state = ParserState.Ground;
                }
                break;

            case ParserState.CsiParam:
                if (c == '\x1b') { _state = ParserState.Escape; break; }
                if (c is >= '0' and <= '9' or ';')
                {
                    _params.Append(c);
                }
                else if (c is >= ' ' and <= '/')
                {
                    _intermediates.Append(c);
                    _state = ParserState.CsiIntermediate;
                }
                else
                {
                    ExecuteCsi(c);
                    _state = ParserState.Ground;
                }
                break;

            case ParserState.CsiIntermediate:
                if (c == '\x1b') { _state = ParserState.Escape; break; }
                if (c is >= ' ' and <= '/')
                {
                    _intermediates.Append(c);
                }
                else
                {
                    ExecuteCsi(c);
                    _state = ParserState.Ground;
                }
                break;

            case ParserState.OscString:
                if (c == '\x07') // BEL terminates OSC
                {
                    _state = ParserState.Ground;
                }
                else if (c == '\x1b')
                {
                    // Could be ESC \ (ST) - transition to escape to handle the backslash
                    _state = ParserState.OscStringEscape;
                }
                // Otherwise accumulate (ignored for now)
                break;

            case ParserState.OscStringEscape:
                // After ESC inside OSC - if '\' then ST terminates, otherwise back to OSC
                if (c == '\\')
                {
                    _state = ParserState.Ground;
                }
                else
                {
                    // Not a valid ST, re-enter OSC
                    _state = ParserState.OscString;
                }
                break;
        }
    }

    private int[] GetParams(int defaultValue = 0)
    {
        if (_params.Length == 0)
            return [defaultValue];

        var parts = _params.ToString().Split(';');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            result[i] = int.TryParse(parts[i], out int val) ? val : defaultValue;
        }
        return result;
    }

    private void ExecuteCsi(char final)
    {
        var p = GetParams();

        if (_privateMode)
        {
            ExecutePrivateCsi(final, p);
            return;
        }

        switch (final)
        {
            case 'A': // Cursor Up
                _buffer.CursorRow = Math.Max(0, _buffer.CursorRow - Math.Max(1, p[0]));
                break;
            case 'B': // Cursor Down
                _buffer.CursorRow = Math.Min(_buffer.Rows - 1, _buffer.CursorRow + Math.Max(1, p[0]));
                break;
            case 'C': // Cursor Forward
                _buffer.CursorCol = Math.Min(_buffer.Columns - 1, _buffer.CursorCol + Math.Max(1, p[0]));
                break;
            case 'D': // Cursor Back
                _buffer.CursorCol = Math.Max(0, _buffer.CursorCol - Math.Max(1, p[0]));
                break;
            case 'E': // Cursor Next Line
                _buffer.CursorCol = 0;
                _buffer.CursorRow = Math.Min(_buffer.Rows - 1, _buffer.CursorRow + Math.Max(1, p[0]));
                break;
            case 'F': // Cursor Previous Line
                _buffer.CursorCol = 0;
                _buffer.CursorRow = Math.Max(0, _buffer.CursorRow - Math.Max(1, p[0]));
                break;
            case 'G': // Cursor Horizontal Absolute
                _buffer.CursorCol = Math.Clamp(p[0] - 1, 0, _buffer.Columns - 1);
                break;
            case 'H': // Cursor Position
            case 'f':
                {
                    int row = p.Length > 0 ? Math.Max(1, p[0]) : 1;
                    int col = p.Length > 1 ? Math.Max(1, p[1]) : 1;
                    _buffer.SetCursorPosition(row - 1, col - 1);
                }
                break;
            case 'J': // Erase Display
                _buffer.EraseDisplay(p[0]);
                break;
            case 'K': // Erase Line
                _buffer.EraseLine(p[0]);
                break;
            case 'L': // Insert Lines
                _buffer.InsertLines(Math.Max(1, p[0]));
                break;
            case 'M': // Delete Lines
                _buffer.DeleteLines(Math.Max(1, p[0]));
                break;
            case 'S': // Scroll Up
                _buffer.ScrollUp(Math.Max(1, p[0]));
                break;
            case 'T': // Scroll Down
                _buffer.ScrollDown(Math.Max(1, p[0]));
                break;
            case 'd': // Vertical Position Absolute
                _buffer.CursorRow = Math.Clamp(p[0] - 1, 0, _buffer.Rows - 1);
                break;
            case 'm': // SGR - Select Graphic Rendition
                ProcessSgr(p);
                break;
            case 'r': // Set Scroll Region
                {
                    int top = p.Length > 0 ? Math.Max(1, p[0]) - 1 : 0;
                    int bottom = p.Length > 1 ? Math.Max(1, p[1]) - 1 : _buffer.Rows - 1;
                    _buffer.ScrollTop = Math.Clamp(top, 0, _buffer.Rows - 1);
                    _buffer.ScrollBottom = Math.Clamp(bottom, 0, _buffer.Rows - 1);
                    _buffer.SetCursorPosition(0, 0);
                }
                break;
            case 'n': // Device Status Report
                // We'd need to write back to input - skip for now
                break;
            case '@': // Insert Characters
                _buffer.InsertChars(Math.Max(1, p[0]));
                break;
            case 'P': // Delete Characters
                _buffer.DeleteChars(Math.Max(1, p[0]));
                break;
            case 'X': // Erase Characters
                _buffer.EraseChars(Math.Max(1, p[0]));
                break;
        }
    }

    private void ExecutePrivateCsi(char final, int[] p)
    {
        switch (final)
        {
            case 'h': // Set Mode
                foreach (int mode in p)
                {
                    switch (mode)
                    {
                        case 25: // Show cursor
                            _buffer.CursorVisible = true;
                            break;
                        case 1049: // Alternate screen buffer
                            _buffer.EnterAlternateBuffer();
                            break;
                    }
                }
                break;
            case 'l': // Reset Mode
                foreach (int mode in p)
                {
                    switch (mode)
                    {
                        case 25: // Hide cursor
                            _buffer.CursorVisible = false;
                            break;
                        case 1049: // Normal screen buffer
                            _buffer.ExitAlternateBuffer();
                            break;
                    }
                }
                break;
        }
    }

    private void ProcessSgr(int[] p)
    {
        for (int i = 0; i < p.Length; i++)
        {
            switch (p[i])
            {
                case 0: // Reset
                    _buffer.CurrentForeground = 7;
                    _buffer.CurrentBackground = 0;
                    _buffer.CurrentAttributes = CellAttributes.None;
                    break;
                case 1:
                    _buffer.CurrentAttributes |= CellAttributes.Bold;
                    break;
                case 2:
                    _buffer.CurrentAttributes |= CellAttributes.Dim;
                    break;
                case 3:
                    _buffer.CurrentAttributes |= CellAttributes.Italic;
                    break;
                case 4:
                    _buffer.CurrentAttributes |= CellAttributes.Underline;
                    break;
                case 7:
                    _buffer.CurrentAttributes |= CellAttributes.Inverse;
                    break;
                case 9:
                    _buffer.CurrentAttributes |= CellAttributes.Strikethrough;
                    break;
                case 22: // Normal intensity
                    _buffer.CurrentAttributes &= ~(CellAttributes.Bold | CellAttributes.Dim);
                    break;
                case 23:
                    _buffer.CurrentAttributes &= ~CellAttributes.Italic;
                    break;
                case 24:
                    _buffer.CurrentAttributes &= ~CellAttributes.Underline;
                    break;
                case 27:
                    _buffer.CurrentAttributes &= ~CellAttributes.Inverse;
                    break;
                case 29:
                    _buffer.CurrentAttributes &= ~CellAttributes.Strikethrough;
                    break;
                // Standard foreground colors (30-37)
                case >= 30 and <= 37:
                    _buffer.CurrentForeground = (byte)(p[i] - 30);
                    break;
                case 38: // Extended foreground
                    i = ProcessExtendedColor(p, i, foreground: true);
                    break;
                case 39: // Default foreground
                    _buffer.CurrentForeground = 7;
                    break;
                // Standard background colors (40-47)
                case >= 40 and <= 47:
                    _buffer.CurrentBackground = (byte)(p[i] - 40);
                    break;
                case 48: // Extended background
                    i = ProcessExtendedColor(p, i, foreground: false);
                    break;
                case 49: // Default background
                    _buffer.CurrentBackground = 0;
                    break;
                // Bright foreground colors (90-97)
                case >= 90 and <= 97:
                    _buffer.CurrentForeground = (byte)(p[i] - 90 + 8);
                    break;
                // Bright background colors (100-107)
                case >= 100 and <= 107:
                    _buffer.CurrentBackground = (byte)(p[i] - 100 + 8);
                    break;
            }
        }
    }

    private int ProcessExtendedColor(int[] p, int i, bool foreground)
    {
        if (i + 1 >= p.Length) return i;

        if (p[i + 1] == 5 && i + 2 < p.Length) // 256-color
        {
            byte color = (byte)Math.Clamp(p[i + 2], 0, 255);
            if (foreground) _buffer.CurrentForeground = color;
            else _buffer.CurrentBackground = color;
            return i + 2;
        }
        // 24-bit color (38;2;r;g;b) - map to nearest 256-color for now
        if (p[i + 1] == 2 && i + 4 < p.Length)
        {
            // Simplified: store as 256-color index 16-231 approximation
            int r = Math.Clamp(p[i + 2], 0, 255);
            int g = Math.Clamp(p[i + 3], 0, 255);
            int b = Math.Clamp(p[i + 4], 0, 255);
            byte index = (byte)(16 + (r / 51) * 36 + (g / 51) * 6 + (b / 51));
            if (foreground) _buffer.CurrentForeground = index;
            else _buffer.CurrentBackground = index;
            return i + 4;
        }

        return i;
    }
}
