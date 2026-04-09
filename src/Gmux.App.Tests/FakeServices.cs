using Gmux.Core.Models;
using Gmux.Core.Services;

namespace Gmux.App.Tests;

internal sealed class FakeUpdateCheckerService : IUpdateCheckerService
{
    public UpdateCheckResult NextResult { get; set; } = new(true, false, "0.1.0", null, null, "no update");
    public int CallCount { get; private set; }
    public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(NextResult);
    }
}

internal sealed class FakeUpdateDownloadService : IUpdateDownloadService
{
    public string ReturnPath { get; set; } = @"C:\tmp\gray-0.2.0.msi";
    public Exception? ThrowOnDownload { get; set; }
    public bool BlockUntilCancelled { get; set; }

    public async Task<string> DownloadAsync(UpdateCheckResult result, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (ThrowOnDownload is not null)
            throw ThrowOnDownload;

        progress.Report(0.5);
        if (BlockUntilCancelled)
        {
            try { await Task.Delay(-1, cancellationToken); }
            catch (OperationCanceledException) { throw; }
        }
        progress.Report(1.0);
        return ReturnPath;
    }
}

internal sealed class FakeUpdateInstallerService : IUpdateInstallerService
{
    public bool CanInstallValue { get; set; } = true;
    public string? Reason { get; set; }
    public int ApplyCallCount { get; private set; }
    public string? LastMsiPath { get; private set; }

    public bool CanInstall(out string? reason)
    {
        reason = Reason;
        return CanInstallValue;
    }

    public void ApplyAndExit(string msiPath, Action exitAction)
    {
        ApplyCallCount++;
        LastMsiPath = msiPath;
        // Do NOT call exitAction here — tests would terminate.
    }
}
