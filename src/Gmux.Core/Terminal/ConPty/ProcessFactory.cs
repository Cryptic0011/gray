using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Gmux.Core.Terminal.ConPty;

/// <summary>
/// Creates a child process attached to a PseudoConsole.
/// </summary>
public static class ProcessFactory
{
    public static PseudoConsoleProcess Start(PseudoConsole console, string command, string? workingDirectory = null)
    {
        var startupInfo = ConfigureStartupInfo(console);
        try
        {
            var processInfo = RunProcess(ref startupInfo, command, workingDirectory);
            return new PseudoConsoleProcess(processInfo, startupInfo.lpAttributeList);
        }
        catch
        {
            CleanupAttributeList(startupInfo.lpAttributeList);
            throw;
        }
    }

    private static NativeMethods.STARTUPINFOEX ConfigureStartupInfo(PseudoConsole console)
    {
        var startupInfo = new NativeMethods.STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();

        // Get the required attribute list size
        var size = nint.Zero;
        NativeMethods.InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size);

        var attrList = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to init attribute list");

            // We need to pass the HPCON handle through the startup attribute list.
            var consoleHandle = GetConsoleHandle(console);

            if (!NativeMethods.UpdateProcThreadAttribute(
                attrList,
                0,
                (nint)NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                consoleHandle,
                (nint)nint.Size,
                nint.Zero,
                nint.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set pseudo console attribute");
            }

            startupInfo.lpAttributeList = attrList;
            return startupInfo;
        }
        catch
        {
            CleanupAttributeList(attrList);
            throw;
        }
    }

    private static nint GetConsoleHandle(PseudoConsole console) => console.Handle;

    private static NativeMethods.PROCESS_INFORMATION RunProcess(
        ref NativeMethods.STARTUPINFOEX startupInfo,
        string command,
        string? workingDirectory)
    {
        if (!NativeMethods.CreateProcessW(
            null,
            command,
            nint.Zero,
            nint.Zero,
            false,
            NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_UNICODE_ENVIRONMENT,
            nint.Zero,
            workingDirectory,
            ref startupInfo,
            out var processInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create process");
        }

        return processInfo;
    }

    private static void CleanupAttributeList(nint attributeList)
    {
        if (attributeList == nint.Zero)
            return;

        NativeMethods.DeleteProcThreadAttributeList(attributeList);
        Marshal.FreeHGlobal(attributeList);
    }
}
