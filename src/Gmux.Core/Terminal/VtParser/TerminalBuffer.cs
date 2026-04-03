namespace Gmux.Core.Terminal.VtParser;

/// <summary>
/// Read-only snapshot of the terminal buffer for lock-free rendering.
/// </summary>
public class BufferSnapshot
{
    public TerminalCell[,] Cells { get; }
    public int Rows { get; }
    public int Columns { get; }
    public int CursorRow { get; }
    public int CursorCol { get; }
    public bool CursorVisible { get; }

    public BufferSnapshot(TerminalCell[,] cells, int rows, int columns, int cursorRow, int cursorCol, bool cursorVisible)
    {
        Cells = cells;
        Rows = rows;
        Columns = columns;
        CursorRow = cursorRow;
        CursorCol = cursorCol;
        CursorVisible = cursorVisible;
    }

    public TerminalCell GetCell(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Columns)
            return TerminalCell.Empty;
        return Cells[row, col];
    }
}

/// <summary>
/// 2D character grid representing the terminal screen buffer.
/// </summary>
public class TerminalBuffer
{
    private TerminalCell[,] _cells;
    private readonly object _lock = new();
    private int _maxScrollback;

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; set; }
    public int CursorCol { get; set; }
    public bool CursorVisible { get; set; } = true;

    // Current text attributes applied to new characters
    public byte CurrentForeground { get; set; } = 7;
    public byte CurrentBackground { get; set; } = 0;
    public CellAttributes CurrentAttributes { get; set; } = CellAttributes.None;

    // Scroll region (top and bottom row, inclusive)
    public int ScrollTop { get; set; }
    public int ScrollBottom { get; set; }

    // Scrollback buffer — stores lines that scroll off the top
    private readonly List<TerminalCell[]> _scrollback = new();
    public int ScrollbackCount { get { lock (_lock) { return _scrollback.Count; } } }

    // Alternate screen buffer support
    private TerminalCell[,]? _savedCells;
    private int _savedCursorRow, _savedCursorCol;
    public bool IsAlternateBuffer { get; private set; }

    public TerminalBuffer(int columns, int rows, int maxScrollback = 5000)
    {
        Columns = columns;
        Rows = rows;
        _maxScrollback = Math.Max(0, maxScrollback);
        ScrollBottom = rows - 1;
        _cells = new TerminalCell[rows, columns];
        Clear();
    }

    public void SetMaxScrollback(int maxScrollback)
    {
        lock (_lock)
        {
            _maxScrollback = Math.Max(0, maxScrollback);
            while (_scrollback.Count > _maxScrollback)
                _scrollback.RemoveAt(0);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                    _cells[r, c] = TerminalCell.Empty;
            CursorRow = 0;
            CursorCol = 0;
        }
    }

    public TerminalCell GetCell(int row, int col)
    {
        lock (_lock)
        {
            if (row < 0 || row >= Rows || col < 0 || col >= Columns)
                return TerminalCell.Empty;
            return _cells[row, col];
        }
    }

    public void WriteChar(char ch)
    {
        lock (_lock)
        {
            if (ch == '\r')
            {
                CursorCol = 0;
                return;
            }
            if (ch == '\n')
            {
                LineFeedInternal();
                return;
            }
            if (ch == '\b')
            {
                if (CursorCol > 0) CursorCol--;
                return;
            }
            if (ch == '\t')
            {
                // Move to next tab stop (every 8 columns)
                CursorCol = Math.Min(Columns - 1, (CursorCol / 8 + 1) * 8);
                return;
            }
            if (ch == '\a') return; // bell - ignore for now

            if (CursorCol >= Columns)
            {
                CursorCol = 0;
                LineFeedInternal();
            }

            _cells[CursorRow, CursorCol] = new TerminalCell
            {
                Character = ch,
                ForegroundColor = CurrentForeground,
                BackgroundColor = CurrentBackground,
                Attributes = CurrentAttributes
            };
            CursorCol++;
        }
    }

    public void LineFeed()
    {
        lock (_lock)
        {
            LineFeedInternal();
        }
    }

    public void ScrollUp(int lines)
    {
        lock (_lock)
        {
            ScrollUpInternal(lines);
        }
    }

    public void ScrollDown(int lines)
    {
        lock (_lock)
        {
            ScrollDownInternal(lines);
        }
    }

    /// <summary>
    /// Lock-free LineFeed for use within already-locked regions.
    /// </summary>
    private void LineFeedInternal()
    {
        if (CursorRow >= ScrollBottom)
        {
            ScrollUpInternal(1);
        }
        else
        {
            CursorRow++;
        }
    }

    /// <summary>
    /// Lock-free ScrollUp for use within already-locked regions.
    /// </summary>
    private void ScrollUpInternal(int lines)
    {
        for (int n = 0; n < lines; n++)
        {
            // Save the top row to scrollback before it's overwritten
            // (only for the default scroll region in normal buffer)
            if (ScrollTop == 0 && !IsAlternateBuffer)
            {
                var saved = new TerminalCell[Columns];
                for (int c = 0; c < Columns; c++)
                    saved[c] = _cells[0, c];
                _scrollback.Add(saved);
                if (_scrollback.Count > _maxScrollback)
                    _scrollback.RemoveAt(0);
            }

            for (int r = ScrollTop; r < ScrollBottom; r++)
                for (int c = 0; c < Columns; c++)
                    _cells[r, c] = _cells[r + 1, c];

            // Clear the bottom row of the scroll region
            for (int c = 0; c < Columns; c++)
                _cells[ScrollBottom, c] = TerminalCell.Empty;
        }
    }

    /// <summary>
    /// Lock-free ScrollDown for use within already-locked regions.
    /// </summary>
    private void ScrollDownInternal(int lines)
    {
        for (int n = 0; n < lines; n++)
        {
            for (int r = ScrollBottom; r > ScrollTop; r--)
                for (int c = 0; c < Columns; c++)
                    _cells[r, c] = _cells[r - 1, c];

            for (int c = 0; c < Columns; c++)
                _cells[ScrollTop, c] = TerminalCell.Empty;
        }
    }

    public void EraseDisplay(int mode)
    {
        lock (_lock)
        {
            switch (mode)
            {
                case 0: // Erase from cursor to end
                    for (int c = CursorCol; c < Columns; c++)
                        _cells[CursorRow, c] = TerminalCell.Empty;
                    for (int r = CursorRow + 1; r < Rows; r++)
                        for (int c = 0; c < Columns; c++)
                            _cells[r, c] = TerminalCell.Empty;
                    break;
                case 1: // Erase from start to cursor
                    for (int r = 0; r < CursorRow; r++)
                        for (int c = 0; c < Columns; c++)
                            _cells[r, c] = TerminalCell.Empty;
                    for (int c = 0; c <= CursorCol; c++)
                        _cells[CursorRow, c] = TerminalCell.Empty;
                    break;
                case 2: // Erase entire display
                case 3:
                    for (int r = 0; r < Rows; r++)
                        for (int c = 0; c < Columns; c++)
                            _cells[r, c] = TerminalCell.Empty;
                    break;
            }
        }
    }

    public void EraseLine(int mode)
    {
        lock (_lock)
        {
            switch (mode)
            {
                case 0: // Erase from cursor to end of line
                    for (int c = CursorCol; c < Columns; c++)
                        _cells[CursorRow, c] = TerminalCell.Empty;
                    break;
                case 1: // Erase from start of line to cursor
                    for (int c = 0; c <= CursorCol; c++)
                        _cells[CursorRow, c] = TerminalCell.Empty;
                    break;
                case 2: // Erase entire line
                    for (int c = 0; c < Columns; c++)
                        _cells[CursorRow, c] = TerminalCell.Empty;
                    break;
            }
        }
    }

    public void SetCursorPosition(int row, int col)
    {
        lock (_lock)
        {
            CursorRow = Math.Clamp(row, 0, Rows - 1);
            CursorCol = Math.Clamp(col, 0, Columns - 1);
        }
    }

    public void InsertLines(int count)
    {
        lock (_lock)
        {
            for (int n = 0; n < count; n++)
            {
                for (int r = ScrollBottom; r > CursorRow; r--)
                    for (int c = 0; c < Columns; c++)
                        _cells[r, c] = _cells[r - 1, c];
                for (int c = 0; c < Columns; c++)
                    _cells[CursorRow, c] = TerminalCell.Empty;
            }
        }
    }

    public void DeleteLines(int count)
    {
        lock (_lock)
        {
            for (int n = 0; n < count; n++)
            {
                for (int r = CursorRow; r < ScrollBottom; r++)
                    for (int c = 0; c < Columns; c++)
                        _cells[r, c] = _cells[r + 1, c];
                for (int c = 0; c < Columns; c++)
                    _cells[ScrollBottom, c] = TerminalCell.Empty;
            }
        }
    }

    public void InsertChars(int count)
    {
        lock (_lock)
        {
            InsertCharsInternal(Math.Max(1, count));
        }
    }

    public void DeleteChars(int count)
    {
        lock (_lock)
        {
            DeleteCharsInternal(Math.Max(1, count));
        }
    }

    public void EraseChars(int count)
    {
        lock (_lock)
        {
            EraseCharsInternal(Math.Max(1, count));
        }
    }

    private void InsertCharsInternal(int count)
    {
        if (CursorRow < 0 || CursorRow >= Rows || CursorCol < 0 || CursorCol >= Columns)
            return;

        int width = Columns - CursorCol;
        if (width <= 0)
            return;

        count = Math.Min(count, width);

        for (int c = Columns - 1; c >= CursorCol + count; c--)
            _cells[CursorRow, c] = _cells[CursorRow, c - count];

        for (int c = CursorCol; c < CursorCol + count; c++)
            _cells[CursorRow, c] = TerminalCell.Empty;
    }

    private void DeleteCharsInternal(int count)
    {
        if (CursorRow < 0 || CursorRow >= Rows || CursorCol < 0 || CursorCol >= Columns)
            return;

        int width = Columns - CursorCol;
        if (width <= 0)
            return;

        count = Math.Min(count, width);

        for (int c = CursorCol; c < Columns - count; c++)
            _cells[CursorRow, c] = _cells[CursorRow, c + count];

        for (int c = Columns - count; c < Columns; c++)
            _cells[CursorRow, c] = TerminalCell.Empty;
    }

    private void EraseCharsInternal(int count)
    {
        if (CursorRow < 0 || CursorRow >= Rows || CursorCol < 0 || CursorCol >= Columns)
            return;

        int endCol = Math.Min(Columns, CursorCol + count);
        for (int c = CursorCol; c < endCol; c++)
            _cells[CursorRow, c] = TerminalCell.Empty;
    }

    public void EnterAlternateBuffer()
    {
        if (IsAlternateBuffer) return;
        lock (_lock)
        {
            _savedCells = (TerminalCell[,])_cells.Clone();
            _savedCursorRow = CursorRow;
            _savedCursorCol = CursorCol;
            IsAlternateBuffer = true;
            Clear();
        }
    }

    public void ExitAlternateBuffer()
    {
        if (!IsAlternateBuffer || _savedCells == null) return;
        lock (_lock)
        {
            _cells = _savedCells;
            _savedCells = null;
            CursorRow = _savedCursorRow;
            CursorCol = _savedCursorCol;
            IsAlternateBuffer = false;
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_lock)
        {
            var newCells = new TerminalCell[rows, columns];
            int copyRows = Math.Min(Rows, rows);
            int copyCols = Math.Min(Columns, columns);

            for (int r = 0; r < copyRows; r++)
                for (int c = 0; c < copyCols; c++)
                    newCells[r, c] = _cells[r, c];

            // Fill new cells with empty
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                    if (r >= copyRows || c >= copyCols)
                        newCells[r, c] = TerminalCell.Empty;

            _cells = newCells;
            Columns = columns;
            Rows = rows;
            ScrollBottom = rows - 1;
            CursorRow = Math.Min(CursorRow, rows - 1);
            CursorCol = Math.Min(CursorCol, columns - 1);
        }
    }

    /// <summary>
    /// Get a row from the scrollback buffer. Index 0 = oldest line.
    /// </summary>
    public TerminalCell[] GetScrollbackRow(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _scrollback.Count)
                return new TerminalCell[Columns];
            return _scrollback[index];
        }
    }

    /// <summary>
    /// Creates a read-only snapshot of the current buffer state.
    /// Locks once and copies the entire visible cell grid so the
    /// renderer can read without further locking.
    /// </summary>
    public BufferSnapshot Snapshot()
    {
        lock (_lock)
        {
            var copy = new TerminalCell[Rows, Columns];
            Array.Copy(_cells, copy, _cells.Length);
            return new BufferSnapshot(copy, Rows, Columns, CursorRow, CursorCol, CursorVisible);
        }
    }
}
