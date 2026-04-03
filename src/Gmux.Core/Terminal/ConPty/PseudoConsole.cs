using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gmux.Core.Terminal.ConPty;

/// <summary>
/// Managed wrapper around a Windows Pseudo Console (ConPTY).
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    private nint _handle;
    private bool _disposed;

    public SafeFileHandle InputWriteSide { get; }
    public SafeFileHandle OutputReadSide { get; }

    internal nint Handle => _handle;

    private PseudoConsole(nint handle, SafeFileHandle inputWriteSide, SafeFileHandle outputReadSide)
    {
        _handle = handle;
        InputWriteSide = inputWriteSide;
        OutputReadSide = outputReadSide;
    }

    public static PseudoConsole Create(short columns, short rows)
    {
        SafeFileHandle? inputReadSide = null;
        SafeFileHandle? inputWriteSide = null;
        SafeFileHandle? outputReadSide = null;
        SafeFileHandle? outputWriteSide = null;

        var inputSecurity = new NativeMethods.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };
        var outputSecurity = new NativeMethods.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        try
        {
            if (!NativeMethods.CreatePipe(out inputReadSide, out inputWriteSide, ref inputSecurity, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create input pipe");

            if (!NativeMethods.CreatePipe(out outputReadSide, out outputWriteSide, ref outputSecurity, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create output pipe");

            var size = new NativeMethods.COORD(columns, rows);
            int hr = NativeMethods.CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out var handle);
            if (hr != 0)
                throw new Win32Exception(hr, "Failed to create pseudo console");

            // Close the sides that the ConPTY now owns.
            inputReadSide.Dispose();
            outputWriteSide.Dispose();

            return new PseudoConsole(handle, inputWriteSide, outputReadSide);
        }
        catch
        {
            inputReadSide?.Dispose();
            inputWriteSide?.Dispose();
            outputReadSide?.Dispose();
            outputWriteSide?.Dispose();
            throw;
        }
    }

    public void Resize(short columns, short rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int hr = NativeMethods.ResizePseudoConsole(_handle, new NativeMethods.COORD(columns, rows));
        if (hr != 0)
            throw new Win32Exception(hr, "Failed to resize pseudo console");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMethods.ClosePseudoConsole(_handle);
        _handle = 0;
        InputWriteSide.Dispose();
        OutputReadSide.Dispose();
    }
}
