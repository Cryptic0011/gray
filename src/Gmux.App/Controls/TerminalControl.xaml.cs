using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Gmux.Core.Terminal;
using Gmux.Core.Terminal.VtParser;
using Gmux.App.Helpers;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;

namespace Gmux.App.Controls;

public sealed partial class TerminalControl : UserControl
{
    private TerminalSession? _session;
    private CanvasControl? _canvas;
    private CanvasTextFormat? _textFormat;
    private double _fontSize = 14;
    private float _cellWidth;  // used only for grid math (backgrounds, selection, resize)
    private float _cellHeight;
    private bool _measured;
    private bool _cursorBlinkState = true;
    private DispatcherTimer? _blinkTimer;
    private DispatcherTimer? _renderTimer;
    private bool _renderPending;
    private readonly System.Text.StringBuilder _drawSb = new(256);

    // Text selection state
    private bool _isSelecting;
    private int _selStartRow, _selStartCol;
    private int _selEndRow, _selEndCol;
    private bool _hasSelection;

    public Guid PaneId { get; set; }

    // Monokai Classic palette
    private static readonly Windows.UI.Color[] AnsiColors =
    [
        Color(0x27, 0x28, 0x22), // 0: Black (Monokai background)
        Color(0xf9, 0x26, 0x72), // 1: Red (Monokai Pink)
        Color(0xa6, 0xe2, 0x2e), // 2: Green (Monokai Green)
        Color(0xf4, 0xbf, 0x75), // 3: Yellow
        Color(0x66, 0xd9, 0xef), // 4: Blue (Monokai Cyan)
        Color(0xae, 0x81, 0xff), // 5: Magenta (Monokai Purple)
        Color(0xa1, 0xef, 0xe4), // 6: Cyan
        Color(0xf8, 0xf8, 0xf2), // 7: White (Monokai Foreground)
        Color(0x75, 0x71, 0x5e), // 8: Bright Black (Monokai Comment)
        Color(0xf9, 0x26, 0x72), // 9: Bright Red
        Color(0xa6, 0xe2, 0x2e), // 10: Bright Green
        Color(0xe6, 0xdb, 0x74), // 11: Bright Yellow (Monokai Yellow)
        Color(0x66, 0xd9, 0xef), // 12: Bright Blue
        Color(0xae, 0x81, 0xff), // 13: Bright Magenta
        Color(0xa1, 0xef, 0xe4), // 14: Bright Cyan
        Color(0xf9, 0xf8, 0xf5), // 15: Bright White
    ];

    private static readonly Windows.UI.Color SelectionColor = Windows.UI.Color.FromArgb(100, 0x66, 0xd9, 0xef);

    private static Windows.UI.Color Color(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);

    public TerminalControl()
    {
        InitializeComponent();
        _fontSize = App.SettingsManager.Current.TerminalFontSize;

        _canvas = new CanvasControl();
        _canvas.Draw += TerminalCanvas_Draw;
        _canvas.CreateResources += TerminalCanvas_CreateResources;
        CanvasHost.Children.Add(_canvas);

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blinkTimer.Tick += (s, e) =>
        {
            _cursorBlinkState = !_cursorBlinkState;
            _canvas?.Invalidate();
        };
        _blinkTimer.Start();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60fps
        _renderTimer.Tick += (s, e) =>
        {
            if (_renderPending)
            {
                _renderPending = false;
                _canvas?.Invalidate();
            }
        };
        _renderTimer.Start();

        InputCapture.PreviewKeyDown += OnTerminalPreviewKeyDown;
        InputCapture.CharacterReceived += OnCharacterReceived;
        InputCapture.TextChanged += (s, e) => { if (InputCapture.Text.Length > 0) InputCapture.Text = ""; };

        Tapped += (s, e) => FocusInput();
        Loaded += (s, e) => FocusInput();

        _canvas.PointerPressed += OnCanvasPointerPressed;
        _canvas.PointerMoved += OnCanvasPointerMoved;
        _canvas.PointerReleased += OnCanvasPointerReleased;
        _canvas.PointerWheelChanged += OnCanvasPointerWheelChanged;

        SizeChanged += OnSizeChanged;
    }

    public void FocusInput()
    {
        InputCapture.Focus(FocusState.Programmatic);
    }

    public async Task AttachSession(TerminalSession session)
    {
        DetachSession();
        _session = session;
        _session.OutputChanged += OnSessionOutputChanged;
        _session.ProcessExited += OnSessionProcessExited;
        if (!_session.IsRunning)
            await _session.StartAsync();
    }

    public void DetachSession()
    {
        if (_session == null) return;
        _session.OutputChanged -= OnSessionOutputChanged;
        _session.ProcessExited -= OnSessionProcessExited;
        _session = null;
    }

    private void OnSessionOutputChanged(TerminalSession _)
    {
        _scrollbackOffset = 0; // Snap to live view on new output
        _renderPending = true;
    }

    private void OnSessionProcessExited(TerminalSession _) =>
        DispatcherQueue.TryEnqueue(() => { });

    private void TerminalCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _textFormat = new CanvasTextFormat
        {
            FontFamily = "Cascadia Mono",
            FontSize = (float)_fontSize,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        _measured = false;
    }

    public void ApplySettings(double fontSize)
    {
        _fontSize = fontSize;
        if (_textFormat != null)
            _textFormat.FontSize = (float)_fontSize;
        _measured = false;
        _canvas?.Invalidate();
    }

    private void TerminalCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_textFormat == null) return;
        var ds = args.DrawingSession;

        if (!_measured)
        {
            using var measure = new CanvasTextLayout(ds, new string('X', 100), _textFormat, float.MaxValue, float.MaxValue);
            var p0 = measure.GetCaretPosition(0, false);
            var p100 = measure.GetCaretPosition(100, false);
            _cellWidth = (p100.X - p0.X) / 100f;
            _cellHeight = (float)measure.LayoutBoundsIncludingTrailingWhitespace.Height;
            _measured = true;

            if (_session != null && _canvas != null)
            {
                int cols = Math.Max(1, (int)(_canvas.Size.Width / _cellWidth));
                int rows = Math.Max(1, (int)(_canvas.Size.Height / _cellHeight));
                _session.Resize(cols, rows);
            }
        }

        if (_session == null) return;
        var buffer = _session.Buffer;
        ds.Clear(AnsiColors[0]);

        bool isScrolledBack = IsScrolledBack(buffer);
        var liveSnapshot = isScrolledBack ? null : buffer.Snapshot();
        int renderRows = liveSnapshot?.Rows ?? buffer.Rows;
        int renderCols = liveSnapshot?.Columns ?? buffer.Columns;

        GetNormalizedSelection(out int selR1, out int selC1, out int selR2, out int selC2);

        float cursorX = 0;
        float cursorY = 0;
        bool cursorFound = false;

        for (int row = 0; row < renderRows; row++)
        {
            float y = row * _cellHeight;

            // Backgrounds and selection
            for (int col = 0; col < renderCols; )
            {
                var cell = GetViewportCell(buffer, liveSnapshot, row, col);
                var bgColor = GetAnsiColor(cell.BackgroundColor);
                bool inverse = cell.Attributes.HasFlag(CellAttributes.Inverse);
                if (inverse) bgColor = GetAnsiColor(cell.ForegroundColor);

                bool isSelected = false;
                if (_hasSelection)
                {
                    if (row > selR1 && row < selR2) isSelected = true;
                    else if (selR1 == selR2 && row == selR1 && col >= selC1 && col < selC2) isSelected = true;
                    else if (row == selR1 && row != selR2 && col >= selC1) isSelected = true;
                    else if (row == selR2 && row != selR1 && col < selC2) isSelected = true;
                }

                int runStartCol = col;
                col++;

                // Group contiguous background cells
                while (col < renderCols)
                {
                    bool nextIsSelected = false;
                    if (_hasSelection)
                    {
                        if (row > selR1 && row < selR2) nextIsSelected = true;
                        else if (selR1 == selR2 && row == selR1 && col >= selC1 && col < selC2) nextIsSelected = true;
                        else if (row == selR1 && row != selR2 && col >= selC1) nextIsSelected = true;
                        else if (row == selR2 && row != selR1 && col < selC2) nextIsSelected = true;
                    }

                    if (nextIsSelected != isSelected) break;

                    if (!isSelected)
                    {
                        var nextCell = GetViewportCell(buffer, liveSnapshot, row, col);
                        var nextBgColor = GetAnsiColor(nextCell.BackgroundColor);
                        bool nextInverse = nextCell.Attributes.HasFlag(CellAttributes.Inverse);
                        if (nextInverse) nextBgColor = GetAnsiColor(nextCell.ForegroundColor);

                        if (nextBgColor != bgColor) break;
                        if ((nextCell.BackgroundColor != 0 || nextInverse) != (cell.BackgroundColor != 0 || inverse)) break;
                    }
                    col++;
                }

                if (isSelected)
                {
                    float curX = (float)Math.Round(runStartCol * _cellWidth);
                    float nextX = (float)Math.Round(col * _cellWidth);
                    ds.FillRectangle(curX, y, nextX - curX, _cellHeight, SelectionColor);
                }
                else if (cell.BackgroundColor != 0 || inverse)
                {
                    float curX = (float)Math.Round(runStartCol * _cellWidth);
                    float nextX = (float)Math.Round(col * _cellWidth);
                    ds.FillRectangle(curX, y, nextX - curX, _cellHeight, bgColor);
                }
            }

            // Text and decorations
            for (int col = 0; col < renderCols; )
            {
                var cell = GetViewportCell(buffer, liveSnapshot, row, col);
                char ch = cell.Character;

                int runStartCol = col;
                byte fg = cell.ForegroundColor;
                byte bg = cell.BackgroundColor;
                var attrs = cell.Attributes;

                bool isEmpty = (ch == '\0' || ch == ' ');
                bool isAscii = ch >= 32 && ch <= 126;
                bool isGrouping = isAscii || isEmpty;

                _drawSb.Clear();
                _drawSb.Append(ch == '\0' ? ' ' : ch);

                col++;

                // Group ASCII/space characters
                while (col < renderCols)
                {
                    var nextCell = GetViewportCell(buffer, liveSnapshot, row, col);
                    char nextCh = nextCell.Character;
                    bool nextIsEmpty = (nextCh == '\0' || nextCh == ' ');
                    bool nextIsAscii = nextCh >= 32 && nextCh <= 126;

                    if (nextCell.ForegroundColor != fg || 
                        nextCell.BackgroundColor != bg || 
                        nextCell.Attributes != attrs)
                    {
                        break;
                    }

                    if (!nextIsAscii && !nextIsEmpty) break;
                    if (!isGrouping) break; // First char was special, so run length is 1

                    _drawSb.Append(nextCh == '\0' ? ' ' : nextCh);
                    col++;
                }

                int runLength = col - runStartCol;
                float x = (float)Math.Round(runStartCol * _cellWidth);
                float endX = (float)Math.Round(col * _cellWidth);
                string text = _drawSb.ToString();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    var fgColor = attrs.HasFlag(CellAttributes.Inverse)
                        ? GetAnsiColor(bg)
                        : GetAnsiColor(fg);
                    
                    ds.DrawText(text, new Vector2(x, y), fgColor, _textFormat);
                }

                if (attrs.HasFlag(CellAttributes.Underline))
                {
                    ds.DrawLine(x, y + _cellHeight - 1, endX, y + _cellHeight - 1, GetAnsiColor(fg));
                }
                if (attrs.HasFlag(CellAttributes.Strikethrough))
                {
                    float midY = y + _cellHeight / 2;
                    ds.DrawLine(x, midY, endX, midY, GetAnsiColor(fg));
                }
            }

            int cursorRow = liveSnapshot?.CursorRow ?? buffer.CursorRow;
            int cursorCol = liveSnapshot?.CursorCol ?? buffer.CursorCol;
            if (row == cursorRow)
            {
                cursorX = (float)Math.Round(cursorCol * _cellWidth);
                cursorY = y;
                cursorFound = true;
            }
        }

        bool cursorVisible = liveSnapshot?.CursorVisible ?? buffer.CursorVisible;
        if (cursorFound && cursorVisible && _cursorBlinkState && !isScrolledBack)
        {
            ds.FillRectangle(cursorX, cursorY, 2, _cellHeight,
                Windows.UI.Color.FromArgb(220, 0xf8, 0xf8, 0xf2));
        }
    }

    private Windows.UI.Color GetAnsiColor(byte index)
    {
        if (index < 16) return AnsiColors[index];

        if (index < 232)
        {
            int i = index - 16;
            int r = (i / 36) * 51;
            int g = ((i / 6) % 6) * 51;
            int b = (i % 6) * 51;
            return Color((byte)r, (byte)g, (byte)b);
        }

        int gray = (index - 232) * 10 + 8;
        return Color((byte)gray, (byte)gray, (byte)gray);
    }

    // --- Text selection ---

    private (int row, int col) HitTest(Windows.Foundation.Point point)
    {
        if (_session == null || _cellWidth == 0) return (0, 0);
        int col = Math.Clamp((int)(point.X / _cellWidth), 0, _session.Buffer.Columns - 1);
        int row = Math.Clamp((int)(point.Y / _cellHeight), 0, _session.Buffer.Rows - 1);
        return (row, col);
    }

    private void GetNormalizedSelection(out int r1, out int c1, out int r2, out int c2)
    {
        if (!_hasSelection)
        {
            r1 = c1 = r2 = c2 = 0;
            return;
        }
        if (_selStartRow < _selEndRow || (_selStartRow == _selEndRow && _selStartCol <= _selEndCol))
        { r1 = _selStartRow; c1 = _selStartCol; r2 = _selEndRow; c2 = _selEndCol; }
        else
        { r1 = _selEndRow; c1 = _selEndCol; r2 = _selStartRow; c2 = _selStartCol; }
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        
        // Let Right-Click fall through to any generic WinUI context menu the user has 
        // hooked up in gmux, rather than hijacking it for copy/paste.

        if (point.Properties.IsLeftButtonPressed)
        {
            var (row, col) = HitTest(point.Position);
            _selStartRow = _selEndRow = row;
            _selStartCol = _selEndCol = col;
            _isSelecting = true;
            _hasSelection = false;
            _canvas?.CapturePointer(e.Pointer);
            _canvas?.Invalidate();
        }
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSelecting) return;
        var point = e.GetCurrentPoint(_canvas);
        var (row, col) = HitTest(point.Position);
        _selEndRow = row;
        _selEndCol = col;
        _hasSelection = (row != _selStartRow || col != _selStartCol);
        _canvas?.Invalidate();
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isSelecting)
        {
            _isSelecting = false;
            _canvas?.ReleasePointerCapture(e.Pointer);
            
            // Auto-copy on select, standard behavior for advanced terminal emulators
            if (_hasSelection)
            {
                CopySelection();
            }
            
            FocusInput(); // Re-focus so keyboard shortcuts work
        }
    }

    // Scrollback viewport offset (0 = live view, positive = scrolled back)
    private int _scrollbackOffset;

    private void OnCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_session == null) return;

        var props = e.GetCurrentPoint(_canvas).Properties;
        int delta = props.MouseWheelDelta;
        int lines = Math.Max(1, Math.Abs(delta) / 40);

        if (_session.Buffer.IsAlternateBuffer)
        {
            // Alternate screen (Claude Code, vim, etc.) — send arrow keys
            string seq = delta > 0 ? "\x1b[A" : "\x1b[B";
            for (int i = 0; i < lines; i++)
                _session.SendInput(seq);
        }
        else
        {
            // Normal buffer — scroll through scrollback history
            if (delta > 0)
                _scrollbackOffset = Math.Min(_scrollbackOffset + lines, _session.Buffer.ScrollbackCount);
            else
                _scrollbackOffset = Math.Max(_scrollbackOffset - lines, 0);
            _canvas?.Invalidate();
        }

        e.Handled = true;
    }

    private bool IsScrolledBack(TerminalBuffer buffer) =>
        _scrollbackOffset > 0 && !buffer.IsAlternateBuffer;

    private TerminalCell GetViewportCell(TerminalBuffer buffer, BufferSnapshot? liveSnapshot, int row, int col)
    {
        if (liveSnapshot != null)
            return liveSnapshot.GetCell(row, col);

        int sbCount = buffer.ScrollbackCount;
        int sbIndex = sbCount - _scrollbackOffset + row;
        if (sbIndex < 0)
            return TerminalCell.Empty;
        if (sbIndex < sbCount)
        {
            var sbRow = buffer.GetScrollbackRow(sbIndex);
            return col < sbRow.Length ? sbRow[col] : TerminalCell.Empty;
        }

        int liveRow = sbIndex - sbCount;
        return buffer.GetCell(liveRow, col);
    }

    private string GetSelectedText()
    {
        if (!_hasSelection || _session == null) return string.Empty;

        GetNormalizedSelection(out int r1, out int c1, out int r2, out int c2);
        var buffer = _session.Buffer;
        var liveSnapshot = IsScrolledBack(buffer) ? null : buffer.Snapshot();
        var sb = new System.Text.StringBuilder();

        for (int row = r1; row <= r2; row++)
        {
            int startCol = (row == r1) ? c1 : 0;
            int endCol = (row == r2) ? c2 : buffer.Columns;

            for (int col = startCol; col < endCol; col++)
            {
                var ch = GetViewportCell(buffer, liveSnapshot, row, col).Character;
                sb.Append(ch == '\0' ? ' ' : ch);
            }

            if (row < r2)
            {
                // Trim trailing spaces before adding newline
                while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                    sb.Length--;
                sb.AppendLine();
            }
        }

        while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
            sb.Length--;

        return sb.ToString();
    }

    private void CopySelection()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);

        // Intentionally do NOT clear _hasSelection here so the highlight stays visible
    }

    // --- Input handling ---

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_session == null) return;

        char ch = e.Character;
        
        // Only block ctrl-characters if we explicitly want to drop them.
        // On Windows/WinUI, KeyDown fires FIRST, which handles our Ctrl+C logic.
        // We just need to stop CharacterReceived from sending the raw ^C character to the shell 
        // if we just copied something.
        if (ch == '\x03' && _hasSelection) { e.Handled = true; return; } 
        if (ch == '\x16') { e.Handled = true; return; } // ^V

        if (_hasSelection)
        {
            _hasSelection = false;
            _canvas?.Invalidate();
        }

        _session.SendInput(ch.ToString());
        e.Handled = true;
    }

    private void OnTerminalPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session == null) return;

        var keyState = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        bool ctrl = keyState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        
        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        bool shift = shiftState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == Windows.System.VirtualKey.C && _hasSelection)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if ((ctrl && e.Key == Windows.System.VirtualKey.V) || 
            (shift && e.Key == Windows.System.VirtualKey.Insert))
        {
            _ = PasteFromClipboard();
            e.Handled = true;
            return;
        }

        var vt = KeyboardHelper.MapKeyToVtSequence(e.Key);
        if (vt != null)
        {
            if (_hasSelection)
            {
                _hasSelection = false;
                _canvas?.Invalidate();
            }

            _session.SendInput(vt);
            e.Handled = true;
        }
    }

    private async Task PasteFromClipboard()
    {
        if (_session == null) return;
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.Text))
        {
            var text = await content.GetTextAsync();
            if (!string.IsNullOrEmpty(text))
                _session.SendInput(text);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_session == null || _cellWidth == 0 || _cellHeight == 0) return;

        int cols = Math.Max(1, (int)(e.NewSize.Width / _cellWidth));
        int rows = Math.Max(1, (int)(e.NewSize.Height / _cellHeight));

        if (cols != _session.Buffer.Columns || rows != _session.Buffer.Rows)
        {
            _session.Resize(cols, rows);
        }
    }
}
