using System.Net.Http.Headers;
using System.Text.Json;
using Gmux.Core.Models;

namespace Gmux.Core.Services;

public class UpdateCheckerService
{
    private readonly HttpClient _httpClient;

    public UpdateCheckerService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("gray", AppInfoService.CurrentVersion));
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var repoUrl = AppInfoService.RepositoryUrl;
        if (!TryParseGitHubRepo(repoUrl, out var owner, out var repo))
        {
            return new UpdateCheckResult(false, false, AppInfoService.CurrentVersion, null, repoUrl,
                "RepositoryUrl is not configured for GitHub releases");
        }

        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(true, false, AppInfoService.CurrentVersion, null, repoUrl,
                    $"GitHub update check failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;
            string? latestVersion = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
            string? releaseUrl = root.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() : repoUrl;

            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                return new UpdateCheckResult(true, false, AppInfoService.CurrentVersion, null, releaseUrl,
                    "Latest release did not contain a tag name");
            }

            var current = NormalizeVersion(AppInfoService.CurrentVersion);
            var latest = NormalizeVersion(latestVersion);
            bool updateAvailable = latest > current;

            return new UpdateCheckResult(true, updateAvailable, AppInfoService.CurrentVersion, latestVersion, releaseUrl,
                updateAvailable ? $"Update available: {latestVersion}" : "You are up to date");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(true, false, AppInfoService.CurrentVersion, null, repoUrl,
                $"Update check failed: {ex.Message}");
        }
    }

    private static bool TryParseGitHubRepo(string? repoUrl, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        if (string.IsNullOrWhiteSpace(repoUrl) || !Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return false;

        owner = segments[0];
        repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        return !(owner.Contains("your-org", StringComparison.OrdinalIgnoreCase) ||
                 repo.Contains("gray", StringComparison.OrdinalIgnoreCase) && repoUrl.Contains("your-org", StringComparison.OrdinalIgnoreCase));
    }

    internal static Version NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return new Version(0, 0, 0, 0);

        var trimmed = version.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[1..];

        var cut = trimmed.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0)
            trimmed = trimmed[..cut];

        return Version.TryParse(trimmed, out var parsed)
            ? parsed
            : new Version(0, 0, 0, 0);
    }
}
