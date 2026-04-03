using Windows.System;

namespace Gmux.App.Helpers;

/// <summary>
/// Maps keyboard input to VT100/xterm escape sequences.
/// </summary>
public static class KeyboardHelper
{
    public static string? MapKeyToVtSequence(VirtualKey key)
    {
        return key switch
        {
            VirtualKey.Up => "\x1b[A",
            VirtualKey.Down => "\x1b[B",
            VirtualKey.Right => "\x1b[C",
            VirtualKey.Left => "\x1b[D",
            VirtualKey.Home => "\x1b[H",
            VirtualKey.End => "\x1b[F",
            VirtualKey.Insert => "\x1b[2~",
            VirtualKey.Delete => "\x1b[3~",
            VirtualKey.PageUp => "\x1b[5~",
            VirtualKey.PageDown => "\x1b[6~",
            VirtualKey.F1 => "\x1bOP",
            VirtualKey.F2 => "\x1bOQ",
            VirtualKey.F3 => "\x1bOR",
            VirtualKey.F4 => "\x1bOS",
            VirtualKey.F5 => "\x1b[15~",
            VirtualKey.F6 => "\x1b[17~",
            VirtualKey.F7 => "\x1b[18~",
            VirtualKey.F8 => "\x1b[19~",
            VirtualKey.F9 => "\x1b[20~",
            VirtualKey.F10 => "\x1b[21~",
            VirtualKey.F11 => "\x1b[23~",
            VirtualKey.F12 => "\x1b[24~",
            VirtualKey.Escape => "\x1b",
            VirtualKey.Tab => "\t",
            VirtualKey.Back => "\x7f",
            VirtualKey.Enter => "\r",
            _ => null
        };
    }
}
