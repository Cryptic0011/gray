using System.Diagnostics;

namespace Gmux.Core.Services;

public class UpdateInstallerService : IUpdateInstallerService
{
    private readonly string _appProcessName;
    private readonly Func<string?> _currentExecutablePathProvider;
    private readonly Action<string, string> _writeAllText;
    private readonly Action<ProcessStartInfo> _startProcess;

    public UpdateInstallerService(
        string appProcessName = "Gmux.App",
        Func<string?>? currentExecutablePathProvider = null,
        Action<string, string>? writeAllText = null,
        Action<ProcessStartInfo>? startProcess = null)
    {
        _appProcessName = appProcessName;
        _currentExecutablePathProvider = currentExecutablePathProvider
            ?? (() => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName);
        _writeAllText = writeAllText ?? File.WriteAllText;
        _startProcess = startProcess ?? (psi => Process.Start(psi));
    }

    public bool CanInstall(out string? reason)
    {
        var count = Process.GetProcessesByName(_appProcessName).Length;
        if (count > 1)
        {
            reason = "Close other gray windows first, then try again.";
            return false;
        }
        reason = null;
        return true;
    }

    public void ApplyAndExit(string msiPath, Action exitAction)
    {
        if (!File.Exists(msiPath))
            throw new FileNotFoundException("Installer not found", msiPath);

        var dir = Path.GetDirectoryName(msiPath) ?? Path.GetTempPath();
        var cmdPath = Path.Combine(dir, "updater.cmd");
        var msiFileName = Path.GetFileName(msiPath);
        var installedExe = ResolveInstalledExePath();

        var script = BuildUpdateScript(_appProcessName, msiFileName, installedExe);

        _writeAllText(cmdPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = cmdPath,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        _startProcess(psi);

        exitAction();
    }

    private string ResolveInstalledExePath()
    {
        var currentExecutablePath = _currentExecutablePathProvider();
        if (!string.IsNullOrWhiteSpace(currentExecutablePath))
            return currentExecutablePath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "gray", "Gmux.App.exe");
    }

    internal static string BuildUpdateScript(string appProcessName, string msiFileName, string installedExe) =>
        $$"""
            @echo off
            :wait
            tasklist /fi "imagename eq {{appProcessName}}.exe" 2>nul | find /i "{{appProcessName}}.exe" >nul
            if not errorlevel 1 (
              timeout /t 1 /nobreak >nul
              goto wait
            )
            msiexec.exe /i "%~dp0{{msiFileName}}" /passive /norestart /l*v "%~dp0install.log"
            if errorlevel 1 (
              copy /y "%~dp0install.log" "%~dp0last-failure.txt" >nul
              exit /b %errorlevel%
            )
            start "" "{{installedExe}}"
            """;
}
