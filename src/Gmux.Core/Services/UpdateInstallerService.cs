using System.Diagnostics;

namespace Gmux.Core.Services;

public class UpdateInstallerService : IUpdateInstallerService
{
    private readonly string _appProcessName;

    public UpdateInstallerService(string appProcessName = "Gmux.App")
    {
        _appProcessName = appProcessName;
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
        var installedExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "gray", "Gmux.App.exe");

        var script = $$"""
            @echo off
            :wait
            tasklist /fi "imagename eq {{_appProcessName}}.exe" 2>nul | find /i "{{_appProcessName}}.exe" >nul
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

        File.WriteAllText(cmdPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = cmdPath,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);

        exitAction();
    }
}
