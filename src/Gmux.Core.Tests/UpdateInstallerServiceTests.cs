using System.Diagnostics;
using Gmux.Core.Services;
using Xunit;

namespace Gmux.Core.Tests;

public class UpdateInstallerServiceTests
{
    [Fact]
    public void ApplyAndExit_UsesCurrentExecutablePathForRelaunch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gray-installer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var msiPath = Path.Combine(tempDir, "gray-0.2.0.msi");
            File.WriteAllText(msiPath, "fake");

            string? writtenPath = null;
            string? writtenScript = null;
            ProcessStartInfo? started = null;
            var exitCalled = false;

            var service = new UpdateInstallerService(
                currentExecutablePathProvider: () => @"D:\Apps\gray\Gmux.App.exe",
                writeAllText: (path, text) =>
                {
                    writtenPath = path;
                    writtenScript = text;
                },
                startProcess: psi => started = psi);

            service.ApplyAndExit(msiPath, () => exitCalled = true);

            Assert.Equal(Path.Combine(tempDir, "updater.cmd"), writtenPath);
            Assert.NotNull(writtenScript);
            Assert.Contains(@"start """" ""D:\Apps\gray\Gmux.App.exe""", writtenScript);
            Assert.NotNull(started);
            Assert.Equal(Path.Combine(tempDir, "updater.cmd"), started!.FileName);
            Assert.True(exitCalled);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BuildUpdateScript_UsesFallbackExecutablePathWhenCurrentPathMissing()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "gray", "Gmux.App.exe");

        var service = new UpdateInstallerService(currentExecutablePathProvider: () => " ");

        string? writtenScript = null;
        ProcessStartInfo? started = null;
        var tempDir = Path.Combine(Path.GetTempPath(), "gray-installer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var msiPath = Path.Combine(tempDir, "gray-0.2.0.msi");
            File.WriteAllText(msiPath, "fake");

            service = new UpdateInstallerService(
                currentExecutablePathProvider: () => " ",
                writeAllText: (_, text) => writtenScript = text,
                startProcess: psi => started = psi);

            service.ApplyAndExit(msiPath, () => { });

            Assert.NotNull(writtenScript);
            Assert.Contains($@"start """" ""{expected}""", writtenScript);
            Assert.NotNull(started);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
