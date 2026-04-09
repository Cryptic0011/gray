using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gmux.Core.Models;
using Gmux.Core.Services;

namespace Gmux.App.ViewModels;

public enum UpdateBannerState
{
    Hidden,
    Available,
    Downloading,
    ReadyToInstall,
    Error,
}

public partial class UpdateBannerViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan CheckThrottle = TimeSpan.FromHours(4);

    private readonly IUpdateCheckerService _checker;
    private readonly IUpdateDownloadService _downloader;
    private readonly IUpdateInstallerService _installer;
    private readonly SettingsManager _settings;
    private readonly Func<Action, Task> _dispatch;
    private readonly Action _exitAction;
    private readonly string _failureFilePath;

    private CancellationTokenSource? _downloadCts;
    private UpdateCheckResult? _current;

    [ObservableProperty] private UpdateBannerState _state = UpdateBannerState.Hidden;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _canViewLog;

    public UpdateBannerViewModel(
        IUpdateCheckerService checker,
        IUpdateDownloadService downloader,
        IUpdateInstallerService installer,
        SettingsManager settings,
        Func<Action, Task>? dispatch = null,
        Action? exitAction = null,
        string? failureFilePath = null)
    {
        _checker = checker;
        _downloader = downloader;
        _installer = installer;
        _settings = settings;
        _dispatch = dispatch ?? (action => { action(); return Task.CompletedTask; });
        _exitAction = exitAction ?? (() => Environment.Exit(0));
        _failureFilePath = failureFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gray", "updates", "last-failure.txt");
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(_failureFilePath))
            {
                var snippet = ReadLastLines(_failureFilePath, 10);
                await _dispatch(() =>
                {
                    State = UpdateBannerState.Error;
                    Title = "Last update failed";
                    Body = snippet;
                    CanViewLog = true;
                });
                return;
            }

            var prefs = _settings.Current.Updates ?? new UpdatePreferences();

            if (prefs.LastCheckUtc.HasValue &&
                DateTime.UtcNow - prefs.LastCheckUtc.Value < CheckThrottle)
            {
                return; // stay Hidden
            }

            var result = await _checker.CheckForUpdatesAsync(ct);
            _current = result;

            prefs.LastCheckUtc = DateTime.UtcNow;
            _settings.Current.Updates = prefs;
            await _settings.SaveAsync();

            if (!result.IsUpdateAvailable)
                return;
            if (!string.IsNullOrEmpty(prefs.SkippedVersion) && prefs.SkippedVersion == result.LatestVersion)
                return;

            await _dispatch(() =>
            {
                State = UpdateBannerState.Available;
                Title = $"gray {result.LatestVersion} is available";
                Body = Truncate(result.ReleaseNotes ?? string.Empty, 200);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateBanner] InitializeAsync failed: {ex.Message}");
            // Swallow — never interrupt launch.
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (_current?.MsiAssetUrl is null)
        {
            ShowError("No installer asset is available for this release.");
            return;
        }

        if (!_installer.CanInstall(out var reason))
        {
            ShowError(reason ?? "Cannot install right now.");
            return;
        }

        _downloadCts = new CancellationTokenSource();
        try
        {
            State = UpdateBannerState.Downloading;
            Title = $"Downloading {_current.LatestVersion}…";
            DownloadProgress = 0;

            var progress = new Progress<double>(p => DownloadProgress = p);
            var msiPath = await _downloader.DownloadAsync(_current, progress, _downloadCts.Token);

            State = UpdateBannerState.ReadyToInstall;
            Title = $"Installing {_current.LatestVersion} — gray will restart";
            Body = string.Empty;

            _installer.ApplyAndExit(msiPath, _exitAction);
        }
        catch (OperationCanceledException)
        {
            State = UpdateBannerState.Available;
            DownloadProgress = 0;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (string.IsNullOrWhiteSpace(_current?.ReleaseUrl)) return;
        Process.Start(new ProcessStartInfo(_current.ReleaseUrl) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Later()
    {
        State = UpdateBannerState.Hidden;
    }

    [RelayCommand]
    private async Task SkipThisVersionAsync()
    {
        if (_current?.LatestVersion is null) return;
        var prefs = _settings.Current.Updates ?? new UpdatePreferences();
        prefs.SkippedVersion = _current.LatestVersion;
        _settings.Current.Updates = prefs;
        await _settings.SaveAsync();
        State = UpdateBannerState.Hidden;
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (CanViewLog && File.Exists(_failureFilePath))
        {
            try { File.Delete(_failureFilePath); } catch { }
            CanViewLog = false;
        }
        State = UpdateBannerState.Hidden;
        await InitializeAsync();
    }

    [RelayCommand]
    private void ViewLog()
    {
        if (!File.Exists(_failureFilePath)) return;
        Process.Start(new ProcessStartInfo(_failureFilePath) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Dismiss()
    {
        State = UpdateBannerState.Hidden;
        if (CanViewLog && File.Exists(_failureFilePath))
        {
            try { File.Delete(_failureFilePath); } catch { }
            CanViewLog = false;
        }
    }

    public void Dispose()
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
    }

    private void ShowError(string message)
    {
        State = UpdateBannerState.Error;
        Title = "Update failed";
        ErrorMessage = message;
        Body = message;
        DownloadProgress = 0;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string ReadLastLines(string path, int count)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            return string.Join("\n", lines.TakeLast(count));
        }
        catch
        {
            return "(couldn't read log)";
        }
    }
}
