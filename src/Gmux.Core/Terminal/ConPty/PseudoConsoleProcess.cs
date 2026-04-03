using System.Runtime.InteropServices;

namespace Gmux.Core.Terminal.ConPty;

/// <summary>
/// Wraps a child process attached to a PseudoConsole.
/// </summary>
public sealed class PseudoConsoleProcess : IDisposable
{
    private readonly NativeMethods.PROCESS_INFORMATION _processInfo;
    private readonly nint _attributeList;
    private bool _disposed;

    public int ProcessId => _processInfo.dwProcessId;

    internal PseudoConsoleProcess(NativeMethods.PROCESS_INFORMATION processInfo, nint attributeList)
    {
        _processInfo = processInfo;
        _attributeList = attributeList;
    }

    public Task WaitForExitAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            NativeMethods.WaitForSingleObject(_processInfo.hProcess, NativeMethods.INFINITE);
        }, ct);
    }

    public bool HasExited
    {
        get
        {
            uint result = NativeMethods.WaitForSingleObject(_processInfo.hProcess, 0);
            return result == NativeMethods.WAIT_OBJECT_0;
        }
    }

    public uint ExitCode
    {
        get
        {
            NativeMethods.GetExitCodeProcess(_processInfo.hProcess, out uint exitCode);
            return exitCode;
        }
    }

    public void Kill()
    {
        NativeMethods.TerminateProcess(_processInfo.hProcess, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        NativeMethods.DeleteProcThreadAttributeList(_attributeList);
        Marshal.FreeHGlobal(_attributeList);

        if (_processInfo.hProcess != nint.Zero)
            NativeMethods.CloseHandle(_processInfo.hProcess);
        if (_processInfo.hThread != nint.Zero)
            NativeMethods.CloseHandle(_processInfo.hThread);
    }
}
