using Gmux.Core.Services;
using Xunit;

namespace Gmux.Core.Tests;

public class TryParseGitHubRepoTests
{
    [Theory]
    [InlineData("https://github.com/Cryptic0011/gray", "Cryptic0011", "gray")]
    [InlineData("https://github.com/Cryptic0011/gray.git", "Cryptic0011", "gray")]
    [InlineData("https://github.com/Cryptic0011/gray/", "Cryptic0011", "gray")]
    [InlineData("https://github.com/Cryptic0011/gray/releases/latest", "Cryptic0011", "gray")]
    [InlineData("https://GITHUB.com/Cryptic0011/gray", "Cryptic0011", "gray")]
    [InlineData("https://github.com/foo-org/some-repo", "foo-org", "some-repo")]
    public void TryParseGitHubRepo_ValidUrls_ReturnsTrue(string url, string expectedOwner, string expectedRepo)
    {
        var ok = UpdateCheckerService.TryParseGitHubRepo(url, out var owner, out var repo);
        Assert.True(ok);
        Assert.Equal(expectedOwner, owner);
        Assert.Equal(expectedRepo, repo);
    }

    [Theory]
    [InlineData("https://gitlab.com/foo/bar")]
    [InlineData("https://example.com/foo/bar")]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com/only-owner")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseGitHubRepo_InvalidUrls_ReturnsFalse(string? url)
    {
        var ok = UpdateCheckerService.TryParseGitHubRepo(url, out _, out _);
        Assert.False(ok);
    }
}
