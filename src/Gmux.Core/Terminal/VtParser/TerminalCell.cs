namespace Gmux.Core.Terminal.VtParser;

/// <summary>
/// A single cell in the terminal character grid.
/// </summary>
public struct TerminalCell
{
    public char Character;
    public byte ForegroundColor; // 0-255 (ANSI 256 palette index)
    public byte BackgroundColor;
    public CellAttributes Attributes;

    public static TerminalCell Empty => new()
    {
        Character = ' ',
        ForegroundColor = 7,  // default white
        BackgroundColor = 0,  // default black
        Attributes = CellAttributes.None
    };
}

[Flags]
public enum CellAttributes : byte
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Inverse = 8,
    Strikethrough = 16,
    Dim = 32
}
