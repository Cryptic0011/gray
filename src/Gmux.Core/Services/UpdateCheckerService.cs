using System.Net.Http.Headers;
using System.Text.Json;
using Gmux.Core.Models;

namespace Gmux.Core.Services;

public class UpdateCheckerService : IUpdateCheckerService
{
    private const string MsiAssetName = "gray-installer-win-x64.msi";

    private readonly HttpClient _httpClient;
    private readonly Func<string> _currentVersionProvider;
    private readonly Func<string?> _repositoryUrlProvider;

    public UpdateCheckerService(
        HttpClient? httpClient = null,
        Func<string>? currentVersionProvider = null,
        Func<string?>? repositoryUrlProvider = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _currentVersionProvider = currentVersionProvider ?? (() => AppInfoService.CurrentVersion);
        _repositoryUrlProvider = repositoryUrlProvider ?? (() => AppInfoService.RepositoryUrl);

        if (_httpClient.Timeout == System.Threading.Timeout.InfiniteTimeSpan || _httpClient.Timeout == TimeSpan.FromSeconds(100))
            _httpClient.Timeout = TimeSpan.FromSeconds(15);

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("gray", _currentVersionProvider() ?? "0.0.0"));
        }

        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        if (!_httpClient.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
            _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var currentVersion = _currentVersionProvider() ?? "0.0.0";
        var repoUrl = _repositoryUrlProvider();

        if (!TryParseGitHubRepo(repoUrl, out var owner, out var repo))
        {
            return new UpdateCheckResult(false, false, currentVersion, null, repoUrl,
                "RepositoryUrl is not configured for GitHub releases");
        }

        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var reason = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
                             values.FirstOrDefault() == "0"
                    ? "GitHub rate limit exceeded"
                    : $"GitHub update check failed: {(int)response.StatusCode} {response.ReasonPhrase}";
                return new UpdateCheckResult(true, false, currentVersion, null, repoUrl, reason);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;

            string? latestVersion = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
            string? releaseUrl = root.TryGetProperty("html_url", out var htmlUrl) ? htmlUrl.GetString() : repoUrl;
            string? releaseNotes = root.TryGetProperty("body", out var body) ? body.GetString() : null;

            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                return new UpdateCheckResult(true, false, currentVersion, null, releaseUrl,
                    "Latest release did not contain a tag name");
            }

            // Skip prerelease tags when the current version is stable.
            bool latestIsPrerelease = latestVersion.Contains('-');
            bool currentIsPrerelease = currentVersion.Contains('-');
            if (latestIsPrerelease && !currentIsPrerelease)
            {
                return new UpdateCheckResult(true, false, currentVersion, latestVersion, releaseUrl,
                    "Latest release is a prerelease; ignored");
            }

            // Find the MSI asset.
            string? msiUrl = null;
            long? msiSize = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var name) &&
                        string.Equals(name.GetString(), MsiAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        msiUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                        msiSize = asset.TryGetProperty("size", out var size) ? size.GetInt64() : null;
                        break;
                    }
                }
            }

            var current = NormalizeVersion(currentVersion);
            var latest = NormalizeVersion(latestVersion);
            bool updateAvailable = latest > current && current > new Version(0, 0, 0, 0);

            return new UpdateCheckResult(
                IsConfigured: true,
                IsUpdateAvailable: updateAvailable,
                CurrentVersion: currentVersion,
                LatestVersion: latestVersion,
                ReleaseUrl: releaseUrl,
                Message: updateAvailable ? $"Update available: {latestVersion}" : "You are up to date",
                ReleaseNotes: releaseNotes,
                MsiAssetUrl: msiUrl,
                MsiAssetSizeBytes: msiSize);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(true, false, currentVersion, null, repoUrl,
                "Update check timed out");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(true, false, currentVersion, null, repoUrl,
                $"Update check failed: {ex.Message}");
        }
    }

    internal static bool TryParseGitHubRepo(string? repoUrl, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;

        if (string.IsNullOrWhiteSpace(repoUrl))
            return false;
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
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
        return !string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo);
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
