namespace Gmux.Core.Models;

public record UpdateCheckResult(
    bool IsConfigured,
    bool IsUpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string Message);
