# Auto-update and PowerShell install script — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship an in-app update flow (check on launch → non-blocking InfoBar banner → user confirms → silent MSI download → apply via `/passive` → auto-relaunch) plus a one-liner PowerShell installer that drives the same MSI. Bundle the prerequisite bug fixes to `UpdateCheckerService` and the release workflow.

**Architecture:** Three new `Gmux.Core` services (`UpdateDownloadService`, `UpdateInstallerService`, `UpdatePreferences`) composed by a new `UpdateBannerViewModel` in `Gmux.App`, rendered by a new `UpdateBanner.xaml` control pinned to the top of `MainWindow`. All three install paths (manual MSI, one-liner, in-app updater) converge on `msiexec /i <msi> /passive /norestart` — the existing WiX MSI is per-user and `MajorUpgrade`-enabled, so no elevation or extra frameworks are needed. Testability is achieved by making the relevant helpers `internal` and exposing them to a new `Gmux.Core.Tests` xUnit project via `InternalsVisibleTo`.

**Tech Stack:** C# 12 / .NET 8 (`net8.0-windows10.0.19041.0`), WinUI 3 / Windows App SDK 1.8, CommunityToolkit.Mvvm 8.4, xUnit 2.9, WiX Toolset 5 (existing), PowerShell 5.1+, GitHub Actions (existing Windows runners).

**Design spec:** `docs/superpowers/specs/2026-04-09-auto-update-and-install-script-design.md` — commit `b71cbec`.

**Working directory:** `C:\Users\GraysonPatterson\Documents\Projects\gmux` (main branch, no worktree).

**File structure:**

```
src/Gmux.Core/
├── Models/
│   ├── UpdateCheckResult.cs            (MODIFY — extend record)
│   └── UpdatePreferences.cs            (CREATE)
├── Services/
│   ├── UpdateCheckerService.cs         (MODIFY — bug fixes, asset parsing, ctor)
│   ├── IUpdateCheckerService.cs        (CREATE — interface)
│   ├── UpdateDownloadService.cs        (CREATE)
│   ├── IUpdateDownloadService.cs       (CREATE — interface)
│   ├── UpdateDownloadException.cs      (CREATE)
│   ├── UpdateInstallerService.cs       (CREATE — writes updater.cmd, exits app)
│   └── IUpdateInstallerService.cs      (CREATE — interface)
├── Gmux.Core.csproj                    (MODIFY — add InternalsVisibleTo)

src/Gmux.Core.Tests/                    (CREATE — new xUnit project)
├── Gmux.Core.Tests.csproj              (CREATE)
├── TestHttpMessageHandler.cs           (CREATE)
├── NormalizeVersionTests.cs            (CREATE)
├── TryParseGitHubRepoTests.cs          (CREATE)
├── UpdateCheckerServiceTests.cs        (CREATE)
├── UpdateDownloadServiceTests.cs       (CREATE)
└── UpdateBannerViewModelTests.cs       (CREATE — lives in a Gmux.App.Tests project)

src/Gmux.App/
├── ViewModels/
│   └── UpdateBannerViewModel.cs        (CREATE)
├── Controls/
│   ├── UpdateBanner.xaml               (CREATE)
│   ├── UpdateBanner.xaml.cs            (CREATE)
│   └── WorkspaceSidebar.xaml.cs        (MODIFY — hyperlink, share VM)
├── MainWindow.xaml                     (MODIFY — add UpdateBanner slot)
├── MainWindow.xaml.cs                  (MODIFY — construct VM, handle Closed)
└── App.xaml.cs                         (MODIFY — remove legacy Task.Run)

src/Gmux.Core/Models/AppSettings.cs     (MODIFY — add Updates property)
src/Gmux.App.Tests/                     (CREATE — xunit test project for VM)
├── Gmux.App.Tests.csproj
└── UpdateBannerViewModelTests.cs

.github/workflows/
├── release.yml                         (MODIFY — inject version into dotnet publish)
└── ci.yml                              (MODIFY — run dotnet test, PSScriptAnalyzer)

install.ps1                             (CREATE — repo root)
README.md                               (MODIFY — install one-liner)
gmux.sln                                (MODIFY — add test projects)
```

> **Project split note:** `UpdateBannerViewModel` lives in `Gmux.App` (depends on `CommunityToolkit.Mvvm`). Tests for it go in a separate `Gmux.App.Tests` project to avoid pulling WinUI into `Gmux.Core.Tests`. If this turns out to be painful (the VM doesn't need WinUI types if written carefully), the engineer may collapse it into `Gmux.Core.Tests` — note that as a decision point at Task 16.

---

## Task 1: Create Gmux.Core.Tests project and verify build

**Files:**
- Create: `src/Gmux.Core.Tests/Gmux.Core.Tests.csproj`
- Create: `src/Gmux.Core.Tests/SanityTest.cs`
- Modify: `gmux.sln`
- Modify: `src/Gmux.Core/Gmux.Core.csproj` (add `InternalsVisibleTo`)

- [ ] **Step 1: Create test project csproj**

Create `src/Gmux.Core.Tests/Gmux.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RootNamespace>Gmux.Core.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <Nullable>enable</Nullable>
    <Platforms>x64;ARM64</Platforms>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Gmux.Core\Gmux.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add a trivial sanity test**

Create `src/Gmux.Core.Tests/SanityTest.cs`:

```csharp
using Xunit;

namespace Gmux.Core.Tests;

public class SanityTest
{
    [Fact]
    public void Truth_IsTrue()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 3: Add InternalsVisibleTo to Gmux.Core**

Modify `src/Gmux.Core/Gmux.Core.csproj` — add this `ItemGroup` before the closing `</Project>`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Gmux.Core.Tests" />
  </ItemGroup>
```

- [ ] **Step 4: Add test project to gmux.sln**

The engineer should use `dotnet sln` to avoid hand-editing GUIDs:

```bash
cd "C:/Users/GraysonPatterson/Documents/Projects/gmux"
dotnet sln gmux.sln add src/Gmux.Core.Tests/Gmux.Core.Tests.csproj
```

Expected: `Project src/Gmux.Core.Tests/Gmux.Core.Tests.csproj added to the solution.`

- [ ] **Step 5: Restore and build**

```bash
dotnet restore gmux.sln
dotnet build gmux.sln --configuration Release --no-restore
```

Expected: build succeeds, all projects compile.

- [ ] **Step 6: Run the sanity test**

```bash
dotnet test src/Gmux.Core.Tests/Gmux.Core.Tests.csproj --configuration Release --no-build
```

Expected: `Passed: 1, Failed: 0, Skipped: 0` (the sanity test passes).

- [ ] **Step 7: Commit**

```bash
git add src/Gmux.Core.Tests/ src/Gmux.Core/Gmux.Core.csproj gmux.sln
git commit -m "test: scaffold Gmux.Core.Tests xunit project

Adds empty xunit project with InternalsVisibleTo so subsequent tasks
can exercise internal helpers in UpdateCheckerService."
```

---

## Task 2: Fix NormalizeVersion (TDD)

**Files:**
- Create: `src/Gmux.Core.Tests/NormalizeVersionTests.cs`
- Modify: `src/Gmux.Core/Services/UpdateCheckerService.cs:88-97` (fix method, change `private` → `internal`)

**Why:** `.NET 8` appends `+<git sha>` to `InformationalVersion` by default, and `Version.TryParse` can't handle `+metadata` or `-prerelease` suffixes. The current implementation silently collapses to `0.0.0.0`, making the checker either always-say-update-available or always-say-up-to-date depending on the tag.

- [ ] **Step 1: Make NormalizeVersion internal (non-behavioral change)**

In `src/Gmux.Core/Services/UpdateCheckerService.cs`, change the method signature on line 88:

```csharp
// Before:
private static Version NormalizeVersion(string version)

// After:
internal static Version NormalizeVersion(string version)
```

- [ ] **Step 2: Write the failing tests**

Create `src/Gmux.Core.Tests/NormalizeVersionTests.cs`:

```csharp
using Gmux.Core.Services;
using Xunit;

namespace Gmux.Core.Tests;

public class NormalizeVersionTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("V0.2.0", "0.2.0")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("0.2.0.0", "0.2.0.0")]
    [InlineData("0.2.0+abc123", "0.2.0")]
    [InlineData("0.2.0-beta.1", "0.2.0")]
    [InlineData("v0.2.0-beta.1+abc123", "0.2.0")]
    [InlineData("  v0.2.0  ", "0.2.0")]
    public void NormalizeVersion_ParsesCommonFormats(string input, string expected)
    {
        var actual = UpdateCheckerService.NormalizeVersion(input);
        Assert.Equal(new System.Version(expected), actual);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("vvv")]
    [InlineData("+abc")]
    [InlineData("-beta")]
    public void NormalizeVersion_UnparseableInput_ReturnsZero(string input)
    {
        var actual = UpdateCheckerService.NormalizeVersion(input);
        Assert.Equal(new System.Version(0, 0, 0, 0), actual);
    }

    [Fact]
    public void NormalizeVersion_Null_ReturnsZero()
    {
        var actual = UpdateCheckerService.NormalizeVersion(null!);
        Assert.Equal(new System.Version(0, 0, 0, 0), actual);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test src/Gmux.Core.Tests/Gmux.Core.Tests.csproj --configuration Release --filter "NormalizeVersionTests"
```

Expected: multiple failures — inputs containing `+` or `-` return `0.0.0.0` instead of the expected `0.2.0`.

- [ ] **Step 4: Fix NormalizeVersion**

Replace the existing method body in `src/Gmux.Core/Services/UpdateCheckerService.cs`:

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test src/Gmux.Core.Tests/Gmux.Core.Tests.csproj --configuration Release --filter "NormalizeVersionTests"
```

Expected: `Passed: 15, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Gmux.Core/Services/UpdateCheckerService.cs src/Gmux.Core.Tests/NormalizeVersionTests.cs
git commit -m "fix(updates): NormalizeVersion handles +metadata and -prerelease

InformationalVersion is now 0.1.0+abc123 after .NET 8's default
SourceRevisionId injection, which Version.TryParse rejects. Strip
the first - or + suffix before parsing so CI-built binaries compare
correctly against release tags."
```

---

## Task 3: Fix TryParseGitHubRepo (TDD)

**Files:**
- Create: `src/Gmux.Core.Tests/TryParseGitHubRepoTests.cs`
- Modify: `src/Gmux.Core/Services/UpdateCheckerService.cs:66-86` (rewrite method, change to `internal`)

**Why:** The current implementation checks `repo.Contains("gray", ...)` as part of a placeholder detector. `gray` is the real repo name, so this accidentally works only because of operator precedence with the surrounding `your-org` check. Replace with a simple "non-empty owner and repo on github.com" check.

- [ ] **Step 1: Make TryParseGitHubRepo internal**

In `src/Gmux.Core/Services/UpdateCheckerService.cs`, change line 66:

```csharp
// Before:
private static bool TryParseGitHubRepo(string? repoUrl, out string owner, out string repo)

// After:
internal static bool TryParseGitHubRepo(string? repoUrl, out string owner, out string repo)
```

- [ ] **Step 2: Write the failing tests**

Create `src/Gmux.Core.Tests/TryParseGitHubRepoTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test src/Gmux.Core.Tests/Gmux.Core.Tests.csproj --configuration Release --filter "TryParseGitHubRepoTests"
```

Expected: several failures. The `gray`-contains check currently short-circuits some valid cases and the tests that assert `true` may still pass accidentally; tests asserting `false` may fail because the current implementation returns true for things it shouldn't.

- [ ] **Step 4: Rewrite TryParseGitHubRepo**

Replace the method body in `src/Gmux.Core/Services/UpdateCheckerService.cs`:

```csharp
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
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test src/Gmux.Core.Tests/Gmux.Core.Tests.csproj --configuration Release --filter "TryParseGitHubRepoTests"
```

Expected: `Passed: 13, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Gmux.Core/Services/UpdateCheckerService.cs src/Gmux.Core.Tests/TryParseGitHubRepoTests.cs
git commit -m "fix(updates): simplify TryParseGitHubRepo

Removes the accidentally-working 'gray' substring placeholder check
that only happened to return the right answer because of operator
precedence. Replaces with a straightforward host+segment validation."
```

---

## Task 4: Extend UpdateCheckResult with MSI asset fields

**Files:**
- Modify: `src/Gmux.Core/Models/UpdateCheckResult.cs`

- [ ] **Step 1: Extend the record**

Replace the entire contents of `src/Gmux.Core/Models/UpdateCheckResult.cs`:

```csharp
namespace Gmux.Core.Models;

public record UpdateCheckResult(
    bool IsConfigured,
    bool IsUpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string Message,
    string? ReleaseNotes = null,
    string? MsiAssetUrl = null,
    long? MsiAssetSizeBytes = null);
```

- [ ] **Step 2: Verify the project still builds**

```bash
dotnet build src/Gmux.Core/Gmux.Core.csproj --configuration Release
```

Expected: build succeeds. The added properties have defaults so existing call sites compile unchanged.

- [ ] **Step 3: Commit**

```bash
git add src/Gmux.Core/Models/UpdateCheckResult.cs
git commit -m "feat(updates): extend UpdateCheckResult with MSI asset metadata"
```

---

## Task 5: UpdateCheckerService — timeout, headers, asset parsing, test seams

**Files:**
- Modify: `src/Gmux.Core/Services/UpdateCheckerService.cs`

**Why:** The current service has no HttpClient timeout, doesn't send GitHub's recommended headers, doesn't parse the `assets` array to find the MSI download URL, and can't be constructed with a custom version/repo for testing. This task bundles all of those.

- [ ] **Step 1: Replace the service body**

Replace the entire contents of `src/Gmux.Core/Services/UpdateCheckerService.cs`:

```csharp
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

            // If no MSI asset, we can still report an update (user can "What's new" to browser), but
            // we can't drive the in-app installer. The banner will handle that by falling back to
            // the browser button.

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
```

- [ ] **Step 2: Create the IUpdateCheckerService interface**

Create `src/Gmux.Core/Services/IUpdateCheckerService.cs`:

```csharp
using Gmux.Core.Models;

namespace Gmux.Core.Services;

public interface IUpdateCheckerService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);
}
```

- [ ] **Step 3: Verify the project still builds and existing tests still pass**

```bash
dotnet build gmux.sln --configuration Release
dotnet test src/Gmux.Core.Tests/Gmux.Core.Tests.csproj --configuration Release --no-build
```

Expected: build succeeds, `NormalizeVersionTests` and `TryParseGitHubRepoTests` still pass (15+13+1 sanity = 29 passed).

- [ ] **Step 4: Commit**

```bash
git add src/Gmux.Core/Services/UpdateCheckerService.cs src/Gmux.Core/Services/IUpdateCheckerService.cs
git commit -m "feat(updates): parse release notes and MSI asset, add timeout and headers

- HttpClient timeout 15s, Accept: vnd.github+json, X-GitHub-Api-Version
- Parse release body into ReleaseNotes
- Find gray-installer-win-x64.msi in assets, expose URL + size
- Skip prerelease tags when current version is stable
- Constructor now accepts Func providers for version/repo URL (test seam)
- Extract IUpdateCheckerService for testability
- Explicit 'current == 0.0.0.0' guard prevents unparseable-current from
  always claiming an update is available"
```

---

## Task 6: UpdateCheckerService end-to-end tests

**Files:**
- Create: `src/Gmux.Core.Tests/TestHttpMessageHandler.cs`
- Create: `src/Gmux.Core.Tests/UpdateCheckerServiceTests.cs`

- [ ] **Step 1: Create the test HTTP handler**

Create `src/Gmux.Core.Tests/TestHttpMessageHandler.cs`:

```csharp
using System.Net;

namespace Gmux.Core.Tests;

/// <summary>
/// HttpMessageHandler that returns canned responses for tests. Not thread-safe.
/// </summary>
internal sealed class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public static TestHttpMessageHandler Json(HttpStatusCode status, string body, Action<HttpResponseHeaders>? configureHeaders = null)
        => new((_, _) =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            configureHeaders?.Invoke(response.Headers);
            return Task.FromResult(response);
        });

    public static TestHttpMessageHandler Throws(Exception ex)
        => new((_, _) => Task.FromException<HttpResponseMessage>(ex));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _handler(request, cancellationToken);
}
```

- [ ] **Step 2: Write the failing tests**

Create `src/Gmux.Core.Tests/UpdateCheckerServiceTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run the tests**

```bash
dotnet test src/Gmux.Core.Tests/Gmux.Core.Tests.csproj --configuration Release --filter "UpdateCheckerServiceTests"
```

Expected: `Passed: 9, Failed: 0`.

- [ ] **Step 4: Commit**

```bash
git add src/Gmux.Core.Tests/TestHttpMessageHandler.cs src/Gmux.Core.Tests/UpdateCheckerServiceTests.cs
git commit -m "test(updates): end-to-end tests for UpdateCheckerService

Uses a canned HttpMessageHandler to exercise:
- happy path with newer tag and MSI asset
- equal tag, prerelease skip, missing MSI asset
- rate limit, server error, timeout
- malformed repo URL (not configured)"
```

---

## Task 7: Fix release.yml to inject version into dotnet publish

**Files:**
- Modify: `.github/workflows/release.yml:42-45`

**Why:** The release workflow publishes `Gmux.App` without `-p:Version`, so the installed assembly always reports `0.1.0` (the hardcoded value in `Directory.Build.props`). The MSI is correctly versioned, but the binary inside it is not — meaning the update checker will always see a mismatch once v0.2.0 ships.

- [ ] **Step 1: Edit the workflow**

In `.github/workflows/release.yml`, find the "Publish App" and "Publish CLI" steps. Replace them with:

```yaml
      - name: Publish App
        run: dotnet publish src/Gmux.App/Gmux.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/app -p:Version=${{ steps.version.outputs.VERSION }} -p:AssemblyVersion=${{ steps.version.outputs.VERSION }}.0 -p:FileVersion=${{ steps.version.outputs.VERSION }}.0 -p:InformationalVersion=${{ steps.version.outputs.VERSION }}

      - name: Publish CLI
        run: dotnet publish src/Gmux.Cli/Gmux.Cli.csproj -c Release -r win-x64 --self-contained false -o artifacts/cli -p:Version=${{ steps.version.outputs.VERSION }} -p:AssemblyVersion=${{ steps.version.outputs.VERSION }}.0 -p:FileVersion=${{ steps.version.outputs.VERSION }}.0 -p:InformationalVersion=${{ steps.version.outputs.VERSION }}
```

- [ ] **Step 2: Syntax-check the workflow**

Workflows can't be run locally easily. At minimum, check the YAML parses:

```bash
python -c "import yaml; yaml.safe_load(open('.github/workflows/release.yml'))" && echo "yaml ok"
```

Expected: `yaml ok`. (If Python isn't available, open the file in VS Code and verify no red squiggles.)

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "fix(release): inject version into dotnet publish

Previously the app binary kept the hardcoded 0.1.0 from
Directory.Build.props even in release builds, so installed users
always saw 'Update available' once a newer tag shipped.

Sets InformationalVersion explicitly (without the +gitSha suffix
that .NET 8 adds by default) for predictable comparison in the
update checker."
```

---

## Task 8: Add UpdatePreferences to AppSettings

**Files:**
- Create: `src/Gmux.Core/Models/UpdatePreferences.cs`
- Modify: `src/Gmux.Core/Models/AppSettings.cs`

- [ ] **Step 1: Create UpdatePreferences**

Create `src/Gmux.Core/Models/UpdatePreferences.cs`:

```csharp
namespace Gmux.Core.Models;

public class UpdatePreferences
{
    public string? SkippedVersion { get; set; }
    public DateTime? LastCheckUtc { get; set; }
}
```

> Note: this is a `class` with mutable properties (not a record), to match the existing `AppSettings` style and to allow direct property assignment from settings panes.

- [ ] **Step 2: Add to AppSettings**

Modify `src/Gmux.Core/Models/AppSettings.cs` — add a new property at the bottom of the class (before the closing brace):

```csharp
    public UpdatePreferences Updates { get; set; } = new();
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Gmux.Core/Gmux.Core.csproj --configuration Release
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Gmux.Core/Models/UpdatePreferences.cs src/Gmux.Core/Models/AppSettings.cs
git commit -m "feat(updates): add UpdatePreferences to AppSettings

Persists SkippedVersion and LastCheckUtc through the existing
SettingsManager JSON file. Missing in old settings files will
deserialize as the default empty object."
```

---

## Task 9: UpdateDownloadException and UpdateDownloadService

**Files:**
- Create: `src/Gmux.Core/Services/UpdateDownloadException.cs`
- Create: `src/Gmux.Core/Services/IUpdateDownloadService.cs`
- Create: `src/Gmux.Core/Services/UpdateDownloadService.cs`

- [ ] **Step 1: Create the exception**

Create `src/Gmux.Core/Services/UpdateDownloadException.cs`:

```csharp
namespace Gmux.Core.Services;

public class UpdateDownloadException : Exception
{
    public UpdateDownloadException(string message) : base(message) { }
    public UpdateDownloadException(string message, Exception inner) : base(message, inner) { }
}
```

- [ ] **Step 2: Create the interface**

Create `src/Gmux.Core/Services/IUpdateDownloadService.cs`:

```csharp
using Gmux.Core.Models;

namespace Gmux.Core.Services;

public interface IUpdateDownloadService
{
    Task<string> DownloadAsync(
        UpdateCheckResult result,
        IProgress<double> progress,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Create the service**

Create `src/Gmux.Core/Services/UpdateDownloadService.cs`:

```csharp
using Gmux.Core.Models;

namespace Gmux.Core.Services;

public class UpdateDownloadService : IUpdateDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly string _downloadDirectory;

    public UpdateDownloadService(HttpClient? httpClient = null, string? downloadDirectory = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gray",
            "updates");
    }

    public async Task<string> DownloadAsync(
        UpdateCheckResult result,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.MsiAssetUrl))
            throw new UpdateDownloadException("No MSI asset URL in update result");
        if (string.IsNullOrWhiteSpace(result.LatestVersion))
            throw new UpdateDownloadException("No version in update result");

        Directory.CreateDirectory(_downloadDirectory);
        var safeVersion = result.LatestVersion.TrimStart('v', 'V');
        var targetPath = Path.Combine(_downloadDirectory, $"gray-{safeVersion}.msi");
        var tempPath = targetPath + ".partial";

        try
        {
            using var response = await _httpClient.GetAsync(
                result.MsiAssetUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new UpdateDownloadException(
                    $"Download failed: {(int)response.StatusCode} {response.ReasonPhrase} ({result.MsiAssetUrl})");

            var total = response.Content.Headers.ContentLength ?? result.MsiAssetSizeBytes ?? 0L;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                    read += n;
                    if (total > 0)
                        progress.Report(Math.Min(1.0, (double)read / total));
                }
            }

            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);
            progress.Report(1.0);
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (UpdateDownloadException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            throw new UpdateDownloadException($"Download failed: {ex.Message}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
```

- [ ] **Step 4: Verify build**

```bash
dotnet build src/Gmux.Core/Gmux.Core.csproj --configuration Release
```

Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Gmux.Core/Services/UpdateDownloadException.cs src/Gmux.Core/Services/IUpdateDownloadService.cs src/Gmux.Core/Services/UpdateDownloadService.cs
git commit -m "feat(updates): add UpdateDownloadService for streaming MSI downloads

Writes to %LocalAppData%/gray/updates/gray-<version>.msi via a
.partial staging file. Reports fractional progress, cleans up on
cancellation or error, and throws UpdateDownloadException on HTTP
or IO failure."
```

---

## Task 10: UpdateDownloadService tests

**Files:**
- Create: `src/Gmux.Core.Tests/UpdateDownloadServiceTests.cs`

- [ ] **Step 1: Write the tests**

Create `src/Gmux.Core.Tests/UpdateDownloadServiceTests.cs`:

```csharp
using System.Net;
using Gmux.Core.Models;
using Gmux.Core.Services;
using Xunit;

namespace Gmux.Core.Tests;

public class UpdateDownloadServiceTests : IDisposable
{
    private readonly string _tempDir;

    public UpdateDownloadServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gray-download-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static UpdateCheckResult MakeResult(string url = "https://example.com/gray.msi") =>
        new(
            IsConfigured: true,
            IsUpdateAvailable: true,
            CurrentVersion: "0.1.0",
            LatestVersion: "v0.2.0",
            ReleaseUrl: "https://example.com/release",
            Message: "Update available",
            MsiAssetUrl: url,
            MsiAssetSizeBytes: 1024);

    [Fact]
    public async Task DownloadAsync_HappyPath_WritesFileAndReportsProgress()
    {
        var payload = new byte[1024];
        new Random(42).NextBytes(payload);

        var handler = new TestHttpMessageHandler((_, _) =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

        var service = new UpdateDownloadService(new HttpClient(handler), _tempDir);
        var progressValues = new List<double>();
        var progress = new Progress<double>(p => progressValues.Add(p));

        var path = await service.DownloadAsync(MakeResult(), progress, CancellationToken.None);

        Assert.True(File.Exists(path));
        var written = await File.ReadAllBytesAsync(path);
        Assert.Equal(payload, written);
        Assert.NotEmpty(progressValues);
        Assert.Equal(1.0, progressValues[^1], precision: 2);
    }

    [Fact]
    public async Task DownloadAsync_404_ThrowsAndLeavesNoPartialFile()
    {
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"not found\"}");
        var service = new UpdateDownloadService(new HttpClient(handler), _tempDir);

        var ex = await Assert.ThrowsAsync<UpdateDownloadException>(
            () => service.DownloadAsync(MakeResult(), new Progress<double>(), CancellationToken.None));
        Assert.Contains("404", ex.Message);
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task DownloadAsync_Cancelled_DeletesPartialFile()
    {
        // Handler that writes slowly so the cancellation can land mid-stream.
        var handler = new TestHttpMessageHandler(async (_, ct) =>
        {
            var stream = new MemoryStream();
            for (int i = 0; i < 1024 * 1024; i++) stream.WriteByte((byte)i);
            stream.Position = 0;
            var content = new StreamContent(stream);
            content.Headers.ContentLength = stream.Length;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var service = new UpdateDownloadService(new HttpClient(handler), _tempDir);
        var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadAsync(MakeResult(), new Progress<double>(), cts.Token));

        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public async Task DownloadAsync_MissingMsiUrl_Throws()
    {
        var handler = TestHttpMessageHandler.Json(HttpStatusCode.OK, "");
        var service = new UpdateDownloadService(new HttpClient(handler), _tempDir);

        await Assert.ThrowsAsync<UpdateDownloadException>(
            () => service.DownloadAsync(MakeResult(url: null!), new Progress<double>(), CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run the tests**

```bash
dotnet test src/Gmux.Core.Tests/Gmux.Core.Tests.csproj --configuration Release --filter "UpdateDownloadServiceTests"
```

Expected: `Passed: 4, Failed: 0`.

- [ ] **Step 3: Commit**

```bash
git add src/Gmux.Core.Tests/UpdateDownloadServiceTests.cs
git commit -m "test(updates): UpdateDownloadService happy-path, 404, cancel, missing-url"
```

---

## Task 11: UpdateInstallerService

**Files:**
- Create: `src/Gmux.Core/Services/IUpdateInstallerService.cs`
- Create: `src/Gmux.Core/Services/UpdateInstallerService.cs`

- [ ] **Step 1: Create the interface**

Create `src/Gmux.Core/Services/IUpdateInstallerService.cs`:

```csharp
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
```

> The `exitAction` parameter is passed in so the service doesn't take a direct dependency on `Microsoft.UI.Xaml.Application` (which would pull WinUI into Gmux.Core). The ViewModel will pass `() => Application.Current.Exit()`.

- [ ] **Step 2: Create the service**

Create `src/Gmux.Core/Services/UpdateInstallerService.cs`:

```csharp
using System.Diagnostics;

namespace Gmux.Core.Services;

public class UpdateInstallerService : IUpdateInstallerService
{
    private readonly string _appProcessName;

    public UpdateInstallerService(string appProcessName = "Gmux.App")
    {
        _appProcessName = appProcessName;
    }

    public bool CanInstall(out string? reason)
    {
        var count = Process.GetProcessesByName(_appProcessName).Length;
        if (count > 1)
        {
            reason = "Close other gray windows first, then try again.";
            return false;
        }
        reason = null;
        return true;
    }

    public void ApplyAndExit(string msiPath, Action exitAction)
    {
        if (!File.Exists(msiPath))
            throw new FileNotFoundException("Installer not found", msiPath);

        var dir = Path.GetDirectoryName(msiPath) ?? Path.GetTempPath();
        var cmdPath = Path.Combine(dir, "updater.cmd");
        var msiFileName = Path.GetFileName(msiPath);
        var installedExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "gray", "Gmux.App.exe");

        var script = $$"""
            @echo off
            :wait
            tasklist /fi "imagename eq {{_appProcessName}}.exe" 2>nul | find /i "{{_appProcessName}}.exe" >nul
            if not errorlevel 1 (
              timeout /t 1 /nobreak >nul
              goto wait
            )
            msiexec.exe /i "%~dp0{{msiFileName}}" /passive /norestart /l*v "%~dp0install.log"
            if errorlevel 1 (
              copy /y "%~dp0install.log" "%~dp0last-failure.txt" >nul
              exit /b %errorlevel%
            )
            start "" "{{installedExe}}"
            """;

        File.WriteAllText(cmdPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = cmdPath,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(psi);

        exitAction();
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Gmux.Core/Gmux.Core.csproj --configuration Release
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Gmux.Core/Services/IUpdateInstallerService.cs src/Gmux.Core/Services/UpdateInstallerService.cs
git commit -m "feat(updates): UpdateInstallerService writes updater.cmd and exits

CanInstall refuses if > 1 Gmux.App process is running. ApplyAndExit
writes a batch script next to the MSI that waits for gray to exit,
runs msiexec /passive, and relaunches. Failure branch copies the
install log to last-failure.txt for next-launch recovery."
```

---

## Task 12: UpdateBannerViewModel

**Files:**
- Create: `src/Gmux.App/ViewModels/UpdateBannerViewModel.cs`

**Notes on design decisions:**
- The VM lives in `Gmux.App` because it depends on `CommunityToolkit.Mvvm`.
- Tests for the VM go in a new `Gmux.App.Tests` project (Task 13) to keep WinUI out of `Gmux.Core.Tests`.
- The VM takes the three service interfaces plus a `DispatcherQueue`-wrapping `Func<Action, Task>` so tests can run synchronously.

- [ ] **Step 1: Create the ViewModel**

Create `src/Gmux.App/ViewModels/UpdateBannerViewModel.cs`:

```csharp
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
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/Gmux.App/Gmux.App.csproj --configuration Release
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Gmux.App/ViewModels/UpdateBannerViewModel.cs
git commit -m "feat(updates): add UpdateBannerViewModel state machine

Drives the five banner states (Hidden, Available, Downloading,
ReadyToInstall, Error) via CommunityToolkit.Mvvm commands. Handles
the 4h throttle, SkippedVersion filter, last-failure recovery, and
cancellation of in-flight downloads on Dispose."
```

---

## Task 13: Gmux.App.Tests project and UpdateBannerViewModel tests

**Files:**
- Create: `src/Gmux.App.Tests/Gmux.App.Tests.csproj`
- Create: `src/Gmux.App.Tests/FakeServices.cs`
- Create: `src/Gmux.App.Tests/UpdateBannerViewModelTests.cs`
- Modify: `gmux.sln`

- [ ] **Step 1: Create the test project csproj**

Create `src/Gmux.App.Tests/Gmux.App.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RootNamespace>Gmux.App.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <Nullable>enable</Nullable>
    <Platforms>x64;ARM64</Platforms>
    <UseWinUI>false</UseWinUI>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Gmux.Core\Gmux.Core.csproj" />
    <Compile Include="..\Gmux.App\ViewModels\UpdateBannerViewModel.cs" Link="UpdateBannerViewModel.cs" />
  </ItemGroup>

</Project>
```

> **Why `Compile Include` instead of `ProjectReference` to Gmux.App?** `Gmux.App` is a WinUI `WinExe` with MSIX tooling enabled — referencing it from a test project pulls in `Microsoft.WindowsAppSDK` and creates MSBuild friction. Compiling the single VM file directly gives us the testable code without the baggage.

- [ ] **Step 2: Create fake services**

Create `src/Gmux.App.Tests/FakeServices.cs`:

```csharp
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
```

- [ ] **Step 3: Write the tests**

Create `src/Gmux.App.Tests/UpdateBannerViewModelTests.cs`:

```csharp
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
```

- [ ] **Step 4: Add test project to solution**

```bash
dotnet sln gmux.sln add src/Gmux.App.Tests/Gmux.App.Tests.csproj
```

- [ ] **Step 5: Restore, build, and run tests**

```bash
dotnet restore gmux.sln
dotnet build gmux.sln --configuration Release --no-restore
dotnet test src/Gmux.App.Tests/Gmux.App.Tests.csproj --configuration Release --no-build
```

Expected: `Passed: 9, Failed: 0`.

> **Note:** These tests use the real `SettingsManager` and therefore read/write `%LocalAppData%\gray\settings.json`. If a developer has local gray settings, the tests may stomp `Updates` fields there. Acceptable for now — document in the test file header if the engineer wants to be cautious.

- [ ] **Step 6: Commit**

```bash
git add src/Gmux.App.Tests/ gmux.sln
git commit -m "test(updates): UpdateBannerViewModel state machine tests

Uses fake services via a separate test project that compiles the
VM source directly (avoids pulling WinUI through a Gmux.App
project reference). Covers: no update, update available, skipped,
throttled, last-failure recovery, install happy path, download
failure, multi-window refusal, skip-this-version persistence."
```

---

## Task 14: UpdateBanner control (XAML + code-behind)

**Files:**
- Create: `src/Gmux.App/Controls/UpdateBanner.xaml`
- Create: `src/Gmux.App/Controls/UpdateBanner.xaml.cs`

- [ ] **Step 1: Create the XAML**

Create `src/Gmux.App/Controls/UpdateBanner.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<UserControl
    x:Class="Gmux.App.Controls.UpdateBanner"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:vm="using:Gmux.App.ViewModels">

    <Grid x:Name="RootGrid">
        <!-- Available / ReadyToInstall / Error share an InfoBar. Downloading overlays a progress bar. -->
        <InfoBar
            x:Name="Bar"
            IsOpen="False"
            IsClosable="True"
            CloseButtonClick="OnCloseClicked"
            Severity="Informational"
            Title="{x:Bind ViewModel.Title, Mode=OneWay}"
            Message="{x:Bind ViewModel.Body, Mode=OneWay}">
            <InfoBar.ActionButton>
                <Button x:Name="PrimaryButton"
                        Content="Install"
                        Click="OnPrimaryClicked" />
            </InfoBar.ActionButton>
        </InfoBar>

        <ProgressBar
            x:Name="DownloadProgressBar"
            Visibility="Collapsed"
            VerticalAlignment="Bottom"
            HorizontalAlignment="Stretch"
            Minimum="0"
            Maximum="1"
            Height="3"
            Value="{x:Bind ViewModel.DownloadProgress, Mode=OneWay}" />
    </Grid>
</UserControl>
```

> **Why code-behind instead of pure XAML bindings for buttons?** InfoBar's `ActionButton` is a single slot, and the button's behavior changes with state (Install → Cancel → Retry). Handling it in code-behind is simpler than building a state-dependent template.

- [ ] **Step 2: Create the code-behind**

Create `src/Gmux.App/Controls/UpdateBanner.xaml.cs`:

```csharp
using Gmux.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gmux.App.Controls;

public sealed partial class UpdateBanner : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(UpdateBannerViewModel),
            typeof(UpdateBanner),
            new PropertyMetadata(null, OnViewModelChanged));

    public UpdateBannerViewModel? ViewModel
    {
        get => (UpdateBannerViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public UpdateBanner()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var banner = (UpdateBanner)d;
        if (e.OldValue is UpdateBannerViewModel old)
            old.PropertyChanged -= banner.OnViewModelPropertyChanged;
        if (e.NewValue is UpdateBannerViewModel newVm)
        {
            newVm.PropertyChanged += banner.OnViewModelPropertyChanged;
            banner.RefreshFromViewModel();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateBannerViewModel.State))
            RefreshFromViewModel();
    }

    private void RefreshFromViewModel()
    {
        if (ViewModel is null) return;
        switch (ViewModel.State)
        {
            case UpdateBannerState.Hidden:
                Bar.IsOpen = false;
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                break;
            case UpdateBannerState.Available:
                Bar.IsOpen = true;
                Bar.Severity = InfoBarSeverity.Informational;
                PrimaryButton.Content = "Install";
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                break;
            case UpdateBannerState.Downloading:
                Bar.IsOpen = true;
                Bar.Severity = InfoBarSeverity.Informational;
                PrimaryButton.Content = "Cancel";
                DownloadProgressBar.Visibility = Visibility.Visible;
                break;
            case UpdateBannerState.ReadyToInstall:
                Bar.IsOpen = true;
                Bar.Severity = InfoBarSeverity.Informational;
                PrimaryButton.Content = "…";
                PrimaryButton.IsEnabled = false;
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                break;
            case UpdateBannerState.Error:
                Bar.IsOpen = true;
                Bar.Severity = InfoBarSeverity.Error;
                PrimaryButton.Content = ViewModel.CanViewLog ? "View log" : "Retry";
                PrimaryButton.IsEnabled = true;
                DownloadProgressBar.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async void OnPrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        switch (ViewModel.State)
        {
            case UpdateBannerState.Available:
                await ViewModel.InstallCommand.ExecuteAsync(null);
                break;
            case UpdateBannerState.Downloading:
                ViewModel.CancelDownloadCommand.Execute(null);
                break;
            case UpdateBannerState.Error:
                if (ViewModel.CanViewLog)
                    ViewModel.ViewLogCommand.Execute(null);
                else
                    await ViewModel.RetryCommand.ExecuteAsync(null);
                break;
        }
    }

    private void OnCloseClicked(InfoBar sender, object args)
    {
        ViewModel?.LaterCommand.Execute(null);
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/Gmux.App/Gmux.App.csproj --configuration Release
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Gmux.App/Controls/UpdateBanner.xaml src/Gmux.App/Controls/UpdateBanner.xaml.cs
git commit -m "feat(updates): add UpdateBanner user control

InfoBar with a single action button whose label and behavior
track ViewModel.State (Install / Cancel / Retry / View log). A
slim ProgressBar at the bottom shows download progress in the
Downloading state."
```

---

## Task 15: Wire UpdateBanner into MainWindow

**Files:**
- Modify: `src/Gmux.App/MainWindow.xaml` (add banner slot above the tab bar)
- Modify: `src/Gmux.App/MainWindow.xaml.cs` (construct VM, Closed handler)
- Modify: `src/Gmux.App/App.xaml.cs` (remove legacy Task.Run, add update service singletons)

- [ ] **Step 1: Add two new service singletons in App.xaml.cs**

In `src/Gmux.App/App.xaml.cs`, find the existing `UpdateChecker` singleton on line 15:

```csharp
    public static UpdateCheckerService UpdateChecker { get; } = new();
```

Leave it as-is (the sidebar in Task 16 keeps using it). Immediately after that line, add two new singletons:

```csharp
    public static UpdateDownloadService UpdateDownloader { get; } = new();
    public static UpdateInstallerService UpdateInstaller { get; } = new();
```

Then **delete** the legacy background update check (currently lines 48-55):

```csharp
        _ = Task.Run(async () =>
        {
            var update = await UpdateChecker.CheckForUpdatesAsync();
            if (update.IsUpdateAvailable)
            {
                NotificationService.Notify("gray", update.Message);
            }
        });
```

The banner ViewModel (wired in Step 3) replaces this.

- [ ] **Step 2: Update MainWindow.xaml**

Modify `src/Gmux.App/MainWindow.xaml`. In the right-side `Grid Grid.Column="2"` (around line 28), change the `Grid.RowDefinitions` from two rows to three:

```xml
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>
```

Change the existing Terminal Pane `Grid Grid.Row="1"` to `Grid.Row="2"`.

Add the banner as a new `Grid.Row="1"` element (after the Tab Bar `Border`, before the Terminal Pane `Grid`):

```xml
            <!-- Update banner -->
            <controls:UpdateBanner
                x:Name="UpdateBannerControl"
                Grid.Row="1" />
```

- [ ] **Step 3: Wire the VM in MainWindow.xaml.cs**

In `src/Gmux.App/MainWindow.xaml.cs`, find the constructor and add the VM construction after `InitializeComponent()`:

```csharp
    private UpdateBannerViewModel? _updateBannerViewModel;

    public MainWindow()
    {
        InitializeComponent();
        // ... existing code ...

        _updateBannerViewModel = new UpdateBannerViewModel(
            App.UpdateChecker,
            App.UpdateDownloader,
            App.UpdateInstaller,
            App.SettingsManager,
            dispatch: action =>
            {
                if (DispatcherQueue.HasThreadAccess)
                {
                    action();
                    return Task.CompletedTask;
                }
                var tcs = new TaskCompletionSource();
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { action(); tcs.SetResult(); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                return tcs.Task;
            },
            exitAction: () => Application.Current.Exit());
        UpdateBannerControl.ViewModel = _updateBannerViewModel;

        Closed += OnClosed;

        _ = _updateBannerViewModel.InitializeAsync();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _updateBannerViewModel?.Dispose();
    }
```

Add the required `using` directives at the top of the file:

```csharp
using Gmux.App.ViewModels;
```

- [ ] **Step 4: Build and smoke-run**

```bash
dotnet build src/Gmux.App/Gmux.App.csproj --configuration Release
```

Expected: build succeeds.

Manual smoke test (optional but recommended at this point):
1. Run `dotnet run --project src/Gmux.App/Gmux.App.csproj --configuration Release`.
2. Verify gray launches as normal, no banner (because local build reports `0.1.0+<sha>` → collapses to `0.1.0` which matches the latest release tag if one exists).
3. Close gray. No exceptions.

- [ ] **Step 5: Commit**

```bash
git add src/Gmux.App/MainWindow.xaml src/Gmux.App/MainWindow.xaml.cs src/Gmux.App/App.xaml.cs
git commit -m "feat(updates): wire UpdateBanner into MainWindow

Adds a new grid row above the terminal pane hosting the
UpdateBanner user control. Constructs the VM with the three
service singletons and the existing SettingsManager, wires the
DispatcherQueue for UI marshaling, and disposes on window close.

Removes the legacy Task.Run-based update check from App.xaml.cs
that only surfaced a silent toast notification."
```

---

## Task 16: Refine WorkspaceSidebar to hyperlink the release URL

**Files:**
- Modify: `src/Gmux.App/Controls/WorkspaceSidebar.xaml.cs:181-199`

**Why:** The existing "Check for updates" button appends the release URL to a `TextBlock` as plain string, which is not clickable. Replace with a `HyperlinkButton`.

- [ ] **Step 1: Locate the existing block**

Open `src/Gmux.App/Controls/WorkspaceSidebar.xaml.cs` and find the block that starts around line 181:

```csharp
var updateStatus = new TextBlock
{
    Text = "Not checked yet",
    ...
};
var checkUpdatesButton = new Button
{
    ...
};
checkUpdatesButton.Click += async (_, _) =>
{
    checkUpdatesButton.IsEnabled = false;
    updateStatus.Text = "Checking GitHub releases...";
    var result = await App.UpdateChecker.CheckForUpdatesAsync();
    updateStatus.Text = result.Message + (string.IsNullOrWhiteSpace(result.ReleaseUrl) ? "" : $" {result.ReleaseUrl}");
    checkUpdatesButton.IsEnabled = true;
};
```

- [ ] **Step 2: Replace with hyperlink-aware version**

Replace that block with:

```csharp
var updateStatus = new TextBlock
{
    Text = "Not checked yet",
    TextWrapping = TextWrapping.WrapWholeWords,
    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x75, 0x71, 0x5e))
};
var releaseLink = new HyperlinkButton
{
    Visibility = Visibility.Collapsed,
    Padding = new Thickness(0),
    HorizontalAlignment = HorizontalAlignment.Left
};
var checkUpdatesButton = new Button
{
    Content = "Check for updates",
    HorizontalAlignment = HorizontalAlignment.Left
};
checkUpdatesButton.Click += async (_, _) =>
{
    checkUpdatesButton.IsEnabled = false;
    updateStatus.Text = "Checking GitHub releases...";
    releaseLink.Visibility = Visibility.Collapsed;

    var result = await App.UpdateChecker.CheckForUpdatesAsync();
    updateStatus.Text = result.Message;
    if (!string.IsNullOrWhiteSpace(result.ReleaseUrl))
    {
        releaseLink.Content = "Open release page";
        releaseLink.NavigateUri = new Uri(result.ReleaseUrl);
        releaseLink.Visibility = Visibility.Visible;
    }
    checkUpdatesButton.IsEnabled = true;
};
```

Then find the block around line 322-323 that adds the existing controls to the container:

```csharp
content.Children.Add(checkUpdatesButton);
content.Children.Add(updateStatus);
```

Add one line immediately after:

```csharp
content.Children.Add(releaseLink);
```

- [ ] **Step 3: Build**

```bash
dotnet build src/Gmux.App/Gmux.App.csproj --configuration Release
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Gmux.App/Controls/WorkspaceSidebar.xaml.cs
git commit -m "feat(updates): hyperlink the release URL in the sidebar

Replaces the plain-text URL concatenation with a HyperlinkButton
that only becomes visible when a result has a ReleaseUrl."
```

---

## Task 17: Create install.ps1

**Files:**
- Create: `install.ps1`

- [ ] **Step 1: Write the script**

Create `install.ps1` at the repository root:

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
    Install (or upgrade) gray from the latest GitHub release.
.DESCRIPTION
    Downloads the MSI asset from the latest (or specified) GitHub release and
    runs it silently. Safe to re-run; the MSI handles major upgrade in place.
.PARAMETER Version
    Pin to a specific release tag (e.g., v0.2.0). Default: latest.
.PARAMETER KeepInstaller
    Do not delete the downloaded MSI after a successful install.
.PARAMETER WhatIf
    Print the resolved MSI URL and exit without downloading. Used by CI smoke.
.EXAMPLE
    iwr https://raw.githubusercontent.com/Cryptic0011/gray/main/install.ps1 -UseBasicParsing | iex
.EXAMPLE
    .\install.ps1 -Version v0.2.0
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [switch]$KeepInstaller,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$Owner = 'Cryptic0011'
$Repo = 'gray'
$AssetName = 'gray-installer-win-x64.msi'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Fail($msg) { Write-Host "!!  $msg" -ForegroundColor Red }
function Write-Ok($msg)   { Write-Host "OK  $msg" -ForegroundColor Green }

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $apiUrl = if ([string]::IsNullOrWhiteSpace($Version)) {
        "https://api.github.com/repos/$Owner/$Repo/releases/latest"
    } else {
        "https://api.github.com/repos/$Owner/$Repo/releases/tags/$Version"
    }

    Write-Step "Resolving release from $apiUrl"
    $release = Invoke-WebRequest -Uri $apiUrl -UseBasicParsing -Headers @{
        'User-Agent' = 'gray-installer'
        'Accept'     = 'application/vnd.github+json'
    } | Select-Object -ExpandProperty Content | ConvertFrom-Json

    $tag = $release.tag_name
    $asset = $release.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
    if (-not $asset) {
        Write-Fail "No $AssetName asset in release $tag"
        Write-Fail "See https://github.com/$Owner/$Repo/releases for available files."
        exit 1
    }

    $msiUrl = $asset.browser_download_url
    Write-Step "Installer URL: $msiUrl"

    if ($WhatIf) {
        Write-Ok "WhatIf mode: not downloading."
        exit 0
    }

    $tempMsi = Join-Path $env:TEMP "gray-installer-$tag.msi"
    Write-Step "Downloading to $tempMsi"
    Invoke-WebRequest -Uri $msiUrl -OutFile $tempMsi -UseBasicParsing -Headers @{
        'User-Agent' = 'gray-installer'
    }
    Unblock-File $tempMsi

    $logPath = Join-Path $env:TEMP 'gray-install.log'
    Write-Step "Running installer (log: $logPath)"
    $proc = Start-Process msiexec.exe -ArgumentList @(
        '/i', "`"$tempMsi`"",
        '/passive',
        '/norestart',
        '/l*v', "`"$logPath`""
    ) -Wait -PassThru

    if ($proc.ExitCode -ne 0) {
        Write-Fail "Install failed with exit code $($proc.ExitCode)"
        if (Test-Path $logPath) {
            Write-Host "--- last 20 log lines ---"
            Get-Content $logPath -Tail 20
            Write-Host "--- full log at: $logPath ---"
        }
        exit $proc.ExitCode
    }

    if (-not $KeepInstaller) {
        Remove-Item $tempMsi -ErrorAction SilentlyContinue
    }

    Write-Ok "gray $tag installed. Run 'gray' or launch from the Start menu."
}
catch [System.Net.WebException] {
    $status = $_.Exception.Response.StatusCode.value__ 2>$null
    if ($status -eq 403) {
        Write-Fail "GitHub rate limit exceeded. Try again later or download the MSI from:"
        Write-Fail "https://github.com/$Owner/$Repo/releases"
    } elseif ($status -eq 404) {
        Write-Fail "Version not found. See https://github.com/$Owner/$Repo/releases for available versions."
    } else {
        Write-Fail "Couldn't reach GitHub: $($_.Exception.Message)"
    }
    exit 1
}
catch {
    Write-Fail $_.Exception.Message
    exit 1
}
```

- [ ] **Step 2: Local smoke check (WhatIf)**

```bash
pwsh -File install.ps1 -WhatIf
```

Expected output (values will vary by what's on GitHub right now):
```
==> Resolving release from https://api.github.com/repos/Cryptic0011/gray/releases/latest
==> Installer URL: https://github.com/Cryptic0011/gray/releases/download/<tag>/gray-installer-win-x64.msi
OK  WhatIf mode: not downloading.
```

If no releases exist yet, the script will fail on the API lookup — that's expected; the script is ready even if you haven't cut a release yet. Skip this step if so.

- [ ] **Step 3: Commit**

```bash
git add install.ps1
git commit -m "feat(install): add PowerShell one-liner installer

Resolves latest release (or a pinned tag), downloads the MSI,
Unblock-File, runs msiexec /passive, prints the log tail on
failure. Supports -Version, -KeepInstaller, -WhatIf."
```

---

## Task 18: CI — run tests and lint install.ps1

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Update CI workflow**

Replace the entire contents of `.github/workflows/ci.yml`:

```yaml
name: CI

on:
  push:
    branches: [ main ]
  pull_request:

jobs:
  build:
    runs-on: windows-latest

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x

      - name: Restore
        run: dotnet restore gmux.sln

      - name: Build
        run: dotnet build gmux.sln --configuration Release --no-restore

      - name: Test Gmux.Core
        run: dotnet test src/Gmux.Core.Tests/Gmux.Core.Tests.csproj --configuration Release --no-build --verbosity normal

      - name: Test Gmux.App (ViewModels)
        run: dotnet test src/Gmux.App.Tests/Gmux.App.Tests.csproj --configuration Release --no-build --verbosity normal

      - name: Lint install.ps1
        shell: pwsh
        run: |
          Install-Module -Name PSScriptAnalyzer -Force -Scope CurrentUser -AllowClobber
          $issues = Invoke-ScriptAnalyzer -Path install.ps1 -Severity Error,Warning
          if ($issues) {
            $issues | Format-Table -AutoSize
            exit 1
          }
          Write-Host "install.ps1: no issues"

      - name: Smoke install.ps1 (WhatIf)
        shell: pwsh
        continue-on-error: true
        run: |
          pwsh -File install.ps1 -WhatIf
        # continue-on-error because this will fail until the first release is cut.
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: run dotnet test, lint install.ps1, smoke -WhatIf

Runs Gmux.Core.Tests and Gmux.App.Tests on every PR. Adds
PSScriptAnalyzer lint on install.ps1 (fail on error or warning).
The -WhatIf smoke step is marked continue-on-error until the
first release tag exists."
```

---

## Task 19: README — install one-liner

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Inspect the current README**

Open `README.md` and locate a sensible place for an Install section — usually just after the project title/description block and before detailed usage docs.

- [ ] **Step 2: Insert the install section**

Add (adjust header level to match surrounding sections):

```markdown
## Install

**Windows PowerShell one-liner:**

    iwr https://raw.githubusercontent.com/Cryptic0011/gray/main/install.ps1 -UseBasicParsing | iex

Or download the MSI directly from the [latest release](https://github.com/Cryptic0011/gray/releases/latest).

Once installed, gray checks GitHub on launch and offers updates through an in-app banner. Updates run silently via the same MSI.

> If Windows SmartScreen warns about the installer, click **More info → Run anyway**. Code signing is on the roadmap.
```

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: add PowerShell one-liner install instructions"
```

---

## Task 20: Manual smoke test checklist

**Files:**
- Create: `docs/superpowers/checklists/2026-04-09-auto-update-smoke.md`

The end-to-end update flow can't be unit-tested (it involves `msiexec`, a real release, and app restart). Document a checklist the release engineer runs once per release.

- [ ] **Step 1: Write the checklist**

Create `docs/superpowers/checklists/2026-04-09-auto-update-smoke.md`:

```markdown
# Auto-update smoke test

Run before every release. Assumes you have an installed copy of the **previous** version of gray and are about to ship a new one.

## Setup
1. Confirm you have gray vN-1 installed via MSI (check Add/Remove Programs).
2. Tag and push vN. Wait for `.github/workflows/release.yml` to publish the release with all three assets (`gray-installer-win-x64.msi`, `gray-app-win-x64.zip`, `gray-cli-win-x64.zip`).
3. Verify the release page shows the expected assets.

## In-app update flow
1. Launch installed gray vN-1. The update banner should appear at the top within a few seconds. Title: `gray vN is available`. Body: first 200 chars of release notes.
2. Click **What's new** → browser opens to the release page. Close browser, return to gray.
3. Click **Install** → banner shows "Downloading vN… X%" with a progress bar.
4. When download completes, banner briefly shows "Installing vN — gray will restart", then gray exits.
5. Wait ~10 seconds. gray relaunches as vN. Confirm by checking sidebar → Settings → Check for updates → should report "You are up to date".

## Skipped version
1. Revert to vN-1 (uninstall vN, reinstall vN-1 from the older MSI).
2. Launch → banner appears. Click **Skip this version**. Banner disappears.
3. Close gray and relaunch. Banner should NOT appear.
4. Verify `%LocalAppData%\gray\settings.json` contains `"SkippedVersion": "vN"`.

## Later
1. Revert to vN-1 again and delete `SkippedVersion` from settings.
2. Launch → banner appears. Click **Later** (or the ✕). Banner disappears.
3. Relaunch gray (without waiting 4 hours). The banner **should** reappear because there's no persistence for Later beyond the current session. Note: there's also a 4h throttle, so if you've already been testing the banner in a tight loop, delete `LastCheckUtc` from settings first.

## Download failure recovery
1. From vN-1 with banner visible, disconnect your network mid-download (or click Install then unplug).
2. Banner transitions to `Error` with "Retry" and "Open in browser" buttons.
3. Reconnect, click **Retry**. Banner returns to `Available`. Click **Install** again → should succeed.

## Multi-window refusal
1. Open gray in two windows (right-click tray / Start menu → launch twice, or `Gmux.App.exe` from two terminals).
2. In one window, click **Install**. Banner transitions to `Error` with "Close other gray windows first, then try again."
3. Close the second window. Click **Retry** → install proceeds normally.

## PowerShell one-liner (fresh install)
1. On a clean Windows VM (or uninstall gray completely first).
2. Open a PowerShell prompt (non-admin).
3. Run: `iwr https://raw.githubusercontent.com/Cryptic0011/gray/main/install.ps1 -UseBasicParsing | iex`
4. Expect `==>` progress messages, an msiexec progress dialog, and `OK  gray vN installed.` at the end.
5. Run `gray --version` (CLI) and launch gray from Start menu. Both should report vN.

## Version pinning
1. `iwr https://raw.githubusercontent.com/Cryptic0011/gray/main/install.ps1 -OutFile $env:TEMP\install.ps1; & $env:TEMP\install.ps1 -Version vN-1`
2. Confirms pinning a specific version works. Useful for CI.

## Cleanup
- `%LocalAppData%\gray\updates\` should be empty after success (or contain only `last-failure.txt` after a failed smoke).
- `%TEMP%\gray-installer-*.msi` should be gone unless `-KeepInstaller` was used.
```

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/checklists/
git commit -m "docs: auto-update smoke test checklist"
```

---

## Self-review

After the above tasks are complete, run through this quick check:

- [ ] All 20 tasks merged (or staged in PRs per the rollout plan in the spec).
- [ ] `dotnet build gmux.sln --configuration Release` succeeds locally.
- [ ] `dotnet test gmux.sln --configuration Release` reports ~32 tests passing (1 sanity + 15 NormalizeVersion + 13 TryParseGitHubRepo + 9 UpdateChecker + 4 UpdateDownload + 9 UpdateBannerViewModel).
- [ ] `pwsh -File install.ps1 -WhatIf` succeeds against the latest release (after the first real release is cut).
- [ ] Manual smoke test from `docs/superpowers/checklists/2026-04-09-auto-update-smoke.md` passes in full.
- [ ] No TODO/TBD strings in any committed source file touched by this plan.

## Rollout (from the spec)

Per the spec's rollout section, the suggested PR sequence is:

1. **PR 1 — "Fix update checker and release workflow"**: Tasks 1-7. Adds `Gmux.Core.Tests`, fixes `NormalizeVersion` and `TryParseGitHubRepo`, extends `UpdateCheckResult`, rewrites `UpdateCheckerService`, and fixes `release.yml`. Independently valuable.
2. **PR 2 — "Add update services and view model"**: Tasks 8-13. Adds `UpdatePreferences`, the download/installer services, the VM, and its tests. Feature-complete but not wired in.
3. **PR 3 — "Wire up update banner in MainWindow"**: Tasks 14-16. User-visible feature-on moment.
4. **PR 4 — "PowerShell installer and CI"**: Tasks 17-20.

If executing linearly (no PR boundaries), just work through the tasks in order.
