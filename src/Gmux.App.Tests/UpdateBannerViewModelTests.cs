using Gmux.App.ViewModels;
using Gmux.Core.Models;
using Gmux.Core.Services;
using Xunit;

namespace Gmux.App.Tests;

public class UpdateBannerViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _failureFile;
    private readonly SettingsManager _settings;

    public UpdateBannerViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gray-vm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _failureFile = Path.Combine(_tempDir, "last-failure.txt");
        _settings = new SettingsManager(); // uses real %LocalAppData%/gray/settings.json
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private UpdateBannerViewModel Create(
        FakeUpdateCheckerService checker,
        FakeUpdateDownloadService? downloader = null,
        FakeUpdateInstallerService? installer = null)
    {
        return new UpdateBannerViewModel(
            checker,
            downloader ?? new FakeUpdateDownloadService(),
            installer ?? new FakeUpdateInstallerService(),
            _settings,
            dispatch: action => { action(); return Task.CompletedTask; },
            exitAction: () => { },
            failureFilePath: _failureFile);
    }

    [Fact]
    public async Task Initialize_NoUpdate_StaysHidden()
    {
        var checker = new FakeUpdateCheckerService
        {
            NextResult = new UpdateCheckResult(true, false, "0.1.0", "v0.1.0", "https://x/", "up to date")
        };
        var vm = Create(checker);
        await vm.InitializeAsync();
        Assert.Equal(UpdateBannerState.Hidden, vm.State);
    }

    [Fact]
    public async Task Initialize_UpdateAvailable_TransitionsToAvailable()
    {
        var checker = new FakeUpdateCheckerService
        {
            NextResult = new UpdateCheckResult(true, true, "0.1.0", "v0.2.0", "https://x/", "Update available: v0.2.0",
                ReleaseNotes: "Faster stuff", MsiAssetUrl: "https://x/msi", MsiAssetSizeBytes: 1024)
        };
        var vm = Create(checker);
        await vm.InitializeAsync();
        Assert.Equal(UpdateBannerState.Available, vm.State);
        Assert.Contains("v0.2.0", vm.Title);
        Assert.Equal("Faster stuff", vm.Body);
    }

    [Fact]
    public async Task Initialize_SkippedVersion_StaysHidden()
    {
        _settings.Current.Updates = new UpdatePreferences { SkippedVersion = "v0.2.0" };
        var checker = new FakeUpdateCheckerService
        {
            NextResult = new UpdateCheckResult(true, true, "0.1.0", "v0.2.0", "https://x/", "Update available: v0.2.0",
                MsiAssetUrl: "https://x/msi")
        };
        var vm = Create(checker);
        await vm.InitializeAsync();
        Assert.Equal(UpdateBannerState.Hidden, vm.State);
    }

    [Fact]
    public async Task Initialize_RecentCheck_SkipsNetworkCall()
    {
        _settings.Current.Updates = new UpdatePreferences { LastCheckUtc = DateTime.UtcNow.AddHours(-1) };
        var checker = new FakeUpdateCheckerService();
        var vm = Create(checker);
        await vm.InitializeAsync();
        Assert.Equal(0, checker.CallCount);
        Assert.Equal(UpdateBannerState.Hidden, vm.State);
    }

    [Fact]
    public async Task Initialize_LastFailureFileExists_TransitionsToErrorWithViewLog()
    {
        await File.WriteAllTextAsync(_failureFile, "line1\nline2\nMSI error 1603");
        var checker = new FakeUpdateCheckerService();
        var vm = Create(checker);
        await vm.InitializeAsync();
        Assert.Equal(UpdateBannerState.Error, vm.State);
        Assert.True(vm.CanViewLog);
    }

    [Fact]
    public async Task Install_HappyPath_CallsApplyAndExit()
    {
        var checker = new FakeUpdateCheckerService
        {
            NextResult = new UpdateCheckResult(true, true, "0.1.0", "v0.2.0", "https://x/", "Update available",
                MsiAssetUrl: "https://x/msi", MsiAssetSizeBytes: 1024)
        };
        var installer = new FakeUpdateInstallerService();
        var vm = Create(checker, installer: installer);
        await vm.InitializeAsync();
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(1, installer.ApplyCallCount);
        Assert.Equal(UpdateBannerState.ReadyToInstall, vm.State);
    }

    [Fact]
    public async Task Install_DownloadFails_TransitionsToError()
    {
        var checker = new FakeUpdateCheckerService
        {
            NextResult = new UpdateCheckResult(true, true, "0.1.0", "v0.2.0", "https://x/", "Update available",
                MsiAssetUrl: "https://x/msi")
        };
        var downloader = new FakeUpdateDownloadService
        {
            ThrowOnDownload = new UpdateDownloadException("boom")
        };
        var vm = Create(checker, downloader: downloader);
        await vm.InitializeAsync();
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(UpdateBannerState.Error, vm.State);
        Assert.Contains("boom", vm.Body);
    }

    [Fact]
    public async Task Install_MultipleWindows_TransitionsToErrorWithReason()
    {
        var checker = new FakeUpdateCheckerService
        {
            NextResult = new UpdateCheckResult(true, true, "0.1.0", "v0.2.0", "https://x/", "Update available",
                MsiAssetUrl: "https://x/msi")
        };
        var installer = new FakeUpdateInstallerService
        {
            CanInstallValue = false,
            Reason = "Close other gray windows first, then try again."
        };
        var vm = Create(checker, installer: installer);
        await vm.InitializeAsync();
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(UpdateBannerState.Error, vm.State);
        Assert.Contains("Close other", vm.Body);
    }

    [Fact]
    public async Task SkipThisVersion_PersistsAndHides()
    {
        var checker = new FakeUpdateCheckerService
        {
            NextResult = new UpdateCheckResult(true, true, "0.1.0", "v0.2.0", "https://x/", "Update available",
                MsiAssetUrl: "https://x/msi")
        };
        var vm = Create(checker);
        await vm.InitializeAsync();
        await vm.SkipThisVersionCommand.ExecuteAsync(null);
        Assert.Equal(UpdateBannerState.Hidden, vm.State);
        Assert.Equal("v0.2.0", _settings.Current.Updates.SkippedVersion);
    }
}
