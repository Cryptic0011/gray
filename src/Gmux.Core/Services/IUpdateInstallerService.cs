namespace Gmux.Core.Services;

public interface IUpdateInstallerService
{
    /// <summary>
    /// Returns true if the installer can safely run right now.
    /// On false, <paramref name="reason"/> holds a user-facing explanation.
    /// </summary>
    bool CanInstall(out string? reason);

    /// <summary>
    /// Writes updater.cmd next to the MSI, starts it detached, and exits the app.
    /// Does not return on success.
    /// </summary>
    void ApplyAndExit(string msiPath, Action exitAction);
}
