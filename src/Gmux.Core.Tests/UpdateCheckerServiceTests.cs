using System.Net;
using Gmux.Core.Services;
using Xunit;

namespace Gmux.Core.Tests;

public class UpdateCheckerServiceTests
{
    private const string RepoUrl = "https://github.com/Cryptic0011/gray";

    private static UpdateCheckerService CreateService(
        TestHttpMessageHandler handler,
        string currentVersion = "0.1.0",
        string? repoUrl = RepoUrl)
    {
        var client = new HttpClient(handler);
        return new UpdateCheckerService(client, () => currentVersion, () => repoUrl);
    }

    private const string ReleaseJsonTemplate = """
    {
      "tag_name": "v0.2.0",
      "html_url": "https://github.com/Cryptic0011/gray/releases/tag/v0.2.0",
      "body": "# Release notes\n\n- Faster startup\n- Bug fixes",
      "assets": [
        {
          "name": "gray-installer-win-x64.msi",
          "browser_download_url": "https://github.com/Cryptic0011/gray/releases/download/v0.2.0/gray-installer-win-x64.msi",
          "size": 12345678
        },
        {
          "name": "gray-app-win-x64.zip",
          "browser_download_url": "https://github.com/Cryptic0011/gray/releases/download/v0.2.0/gray-app-win-x64.zip",
          "size": 54321
        }
      ]
    }
    """;

    [Fact]
    public async Task CheckForUpdates_NewerTag_ReturnsUpdateAvailableWithMsiUrl()
    {
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.OK, ReleaseJsonTemplate);
        var service = CreateService(handler, currentVersion: "0.1.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.True(result.IsConfigured);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.1.0", result.CurrentVersion);
        Assert.Equal("v0.2.0", result.LatestVersion);
        Assert.Equal("https://github.com/Cryptic0011/gray/releases/download/v0.2.0/gray-installer-win-x64.msi", result.MsiAssetUrl);
        Assert.Equal(12345678, result.MsiAssetSizeBytes);
        Assert.Contains("Faster startup", result.ReleaseNotes);
    }

    [Fact]
    public async Task CheckForUpdates_SameTag_ReturnsNoUpdate()
    {
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.OK, ReleaseJsonTemplate);
        var service = CreateService(handler, currentVersion: "0.2.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.True(result.IsConfigured);
        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("You are up to date", result.Message);
    }

    [Fact]
    public async Task CheckForUpdates_CurrentHasGitSha_ComparesCorrectly()
    {
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.OK, ReleaseJsonTemplate);
        var service = CreateService(handler, currentVersion: "0.1.0+abc123");

        var result = await service.CheckForUpdatesAsync();

        Assert.True(result.IsUpdateAvailable); // 0.1.0 < 0.2.0
    }

    [Fact]
    public async Task CheckForUpdates_PrereleaseTag_StableCurrent_Skipped()
    {
        var json = ReleaseJsonTemplate.Replace("\"tag_name\": \"v0.2.0\"", "\"tag_name\": \"v0.2.0-beta.1\"");
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.OK, json);
        var service = CreateService(handler, currentVersion: "0.1.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.False(result.IsUpdateAvailable);
        Assert.Contains("prerelease", result.Message);
    }

    [Fact]
    public async Task CheckForUpdates_RateLimited_ReturnsRateLimitMessage()
    {
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}",
            headers => headers.Add("X-RateLimit-Remaining", "0"));
        var service = CreateService(handler);

        var result = await service.CheckForUpdatesAsync();

        Assert.False(result.IsUpdateAvailable);
        Assert.Contains("rate limit", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckForUpdates_ServerError_ReturnsFailure()
    {
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}");
        var service = CreateService(handler);

        var result = await service.CheckForUpdatesAsync();

        Assert.False(result.IsUpdateAvailable);
        Assert.Contains("500", result.Message);
    }

    [Fact]
    public async Task CheckForUpdates_Timeout_ReturnsTimeoutMessage()
    {
        var handler = TestHttpMessageHandler.Throws(new TaskCanceledException());
        var service = CreateService(handler);

        var result = await service.CheckForUpdatesAsync();

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("Update check timed out", result.Message);
    }

    [Fact]
    public async Task CheckForUpdates_NoMsiAsset_StillReportsUpdateWithNullMsiUrl()
    {
        var json = ReleaseJsonTemplate.Replace("gray-installer-win-x64.msi", "gray-something-else.msi");
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.OK, json);
        var service = CreateService(handler, currentVersion: "0.1.0");

        var result = await service.CheckForUpdatesAsync();

        Assert.True(result.IsUpdateAvailable);
        Assert.Null(result.MsiAssetUrl);
    }

    [Fact]
    public async Task CheckForUpdates_MalformedRepoUrl_ReturnsNotConfigured()
    {
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.OK, ReleaseJsonTemplate);
        var service = CreateService(handler, repoUrl: "not a url");

        var result = await service.CheckForUpdatesAsync();

        Assert.False(result.IsConfigured);
        Assert.False(result.IsUpdateAvailable);
    }
}
