# Auto-update and PowerShell install script — Design

**Date:** 2026-04-09
**Status:** Design approved, ready for implementation planning
**Scope:** Fix the existing update checker, add an in-app update flow (banner → download → silent MSI → auto-relaunch), add a one-liner PowerShell installer.

## Goals

1. **Install once, update forever.** After the initial MSI install, gray checks GitHub Releases on each launch, surfaces updates through a non-blocking banner, and — on user confirmation — downloads and applies the update silently, then relaunches itself.
2. **One-liner install from a fresh shell.** A single `iwr … | iex` command downloads and runs the latest MSI. No admin, no manual clicking through GitHub.
3. **Reuse the existing MSI.** gray already ships a per-user, `MajorUpgrade`-enabled WiX MSI. All three install paths (manual MSI, one-liner, in-app updater) converge on `msiexec /i … /passive /norestart`.

## Non-goals

- Silent background updates with no user confirmation (the user explicitly wants a prompt).
- Delta updates / binary diffs (the MSI is small; re-downloading is fine).
- Portable zip install path via the one-liner (MSI only — option A from brainstorming Q4).
- Code signing the MSI (SmartScreen concern; follow-up).
- SHA verification of downloaded assets (HTTPS from GitHub is acceptable for v1; follow-up).
- Full semver support including prerelease comparison beyond "skip prereleases when on stable."

## Baseline bug fixes (prerequisite)

The existing `UpdateCheckerService` (`src/Gmux.Core/Services/UpdateCheckerService.cs`) has three bugs that prevent *any* update feature from working in production:

1. **Release workflow doesn't inject the version into the app build.** `.github/workflows/release.yml:42-45` runs `dotnet publish` without `-p:Version=${VERSION}`, so the installed app assembly always reports `0.1.0` (the hardcoded value in `Directory.Build.props:9`). The MSI gets the version, but the binary inside it doesn't. **Fix:** pass `-p:Version=${{ steps.version.outputs.VERSION }} -p:AssemblyVersion=${{ steps.version.outputs.VERSION }}.0 -p:FileVersion=${{ steps.version.outputs.VERSION }}.0 -p:InformationalVersion=${{ steps.version.outputs.VERSION }}` to both `dotnet publish` calls.

2. **`NormalizeVersion` collapses to `0.0.0.0` on normal inputs.** `Version.TryParse` can't handle `+metadata` (git SHA suffix appended automatically by .NET 8) or `-prerelease` suffixes. Fix:
   ```csharp
   private static Version NormalizeVersion(string version)
   {
       var trimmed = (version ?? string.Empty).Trim().TrimStart('v', 'V');
       var cut = trimmed.IndexOfAny(new[] { '-', '+' });
       if (cut >= 0) trimmed = trimmed[..cut];
       return Version.TryParse(trimmed, out var parsed) ? parsed : new Version(0, 0, 0, 0);
   }
   ```
   And `updateAvailable` becomes `latest > current && current > new Version(0, 0, 0, 0)` so a failed parse of the current version can't be interpreted as "update always available."

3. **`TryParseGitHubRepo` has a buggy placeholder detector.** Lines 84-85 check `repo.Contains("gray", ...)` — which is the *real* repo name. It accidentally works today only because of operator precedence. **Fix:** drop the `"gray"`/`"your-org"` string checks entirely. Validate the URL parses, host is `github.com`, and owner/repo are non-empty.

Also in the same file:

- Set `_httpClient.Timeout = TimeSpan.FromSeconds(15)`.
- Add `Accept: application/vnd.github+json` and `X-GitHub-Api-Version: 2022-11-28` headers.
- Extend `UpdateCheckResult` with `ReleaseNotes` (string?), `MsiAssetUrl` (string?), `MsiAssetSizeBytes` (long?), all populated by parsing the `body` and `assets` fields from the GitHub response.

## Architecture

### Component map

```
Gmux.Core.Services
├── UpdateCheckerService       (fixed)    — queries GitHub, returns UpdateCheckResult
├── UpdateDownloadService      (new)      — streams MSI to %LocalAppData%\gray\updates
├── UpdateInstallerService     (new)      — writes updater.cmd, launches it, exits the app
└── SettingsManager            (edited)   — persists UpdatePreferences

Gmux.Core.Models
├── UpdateCheckResult          (extended) — adds ReleaseNotes, MsiAssetUrl, MsiAssetSizeBytes
└── UpdatePreferences          (new)      — SkippedVersion, RemindAfterUtc, LastCheckUtc

Gmux.App
├── ViewModels/UpdateBannerViewModel.cs   (new)
├── Controls/UpdateBanner.xaml(.cs)       (new) — wraps Microsoft.UI.Xaml.Controls.InfoBar
├── Controls/WorkspaceSidebar.xaml.cs     (edited) — shares the VM, hyperlink the release URL
└── MainWindow.xaml                       (edited) — InfoBar slot at top

Repository root
└── install.ps1                           (new) — one-liner PowerShell installer

.github/workflows
├── release.yml                           (fixed)   — inject version into dotnet publish
└── ci.yml                                (edited) — run dotnet test, run PSScriptAnalyzer
```

### Data model

```csharp
// Gmux.Core/Models/UpdateCheckResult.cs
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

// Gmux.Core/Models/UpdatePreferences.cs
public record UpdatePreferences
{
    public string? SkippedVersion { get; init; }
    public DateTime? LastCheckUtc { get; init; }
}
```

`UpdatePreferences` is serialized into the existing settings JSON under a new `"updates"` key by `SettingsManager`.

### UpdateDownloadService contract

```csharp
public sealed class UpdateDownloadService
{
    public Task<string> DownloadAsync(
        UpdateCheckResult result,
        IProgress<double> progress,
        CancellationToken cancellationToken);
    // Returns the absolute path to the downloaded MSI.
    // Throws UpdateDownloadException on failure.
    // Writes to %LocalAppData%\gray\updates\gray-<version>.msi
    // Streams in 64 KiB chunks, reporting progress as bytesRead / totalBytes in [0.0, 1.0].
    // On cancellation, deletes the partial file.
}
```

### UpdateInstallerService contract

```csharp
public sealed class UpdateInstallerService
{
    public bool CanInstall(out string? reason);
    // Returns false if > 1 Gmux.App.exe processes are running,
    // or if the updates directory can't be written.

    public void ApplyAndExit(string msiPath);
    // Writes %LocalAppData%\gray\updates\updater.cmd,
    // starts it detached (UseShellExecute=true, WindowStyle=Hidden),
    // then calls Application.Current.Exit().
    // Does NOT return normally on success.
}
```

### updater.cmd template

Written at download time (with `<version>` substituted):

```cmd
@echo off
:wait
tasklist /fi "imagename eq Gmux.App.exe" 2>nul | find /i "Gmux.App.exe" >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)
msiexec.exe /i "%~dp0gray-<version>.msi" /passive /norestart /l*v "%~dp0install.log"
if errorlevel 1 (
  copy /y "%~dp0install.log" "%~dp0last-failure.txt" >nul
  exit /b %errorlevel%
)
start "" "%LOCALAPPDATA%\Programs\gray\Gmux.App.exe"
```

## Update flow

### On app launch

1. `App.OnLaunched` activates the main window.
2. `MainWindow` constructs `UpdateBannerViewModel` and calls `InitializeAsync()` on a background task.
3. `InitializeAsync`:
   - Loads `UpdatePreferences` from settings.
   - Checks for `last-failure.txt` in the updates directory. If present, transitions to `Error` state with log viewer and returns.
   - If `LastCheckUtc` is less than 4 hours ago, returns without hitting the network. (Covers rapid relaunches; any pending "update available" re-prompt is deferred to the next launch that falls outside the 4h window. This is acceptable because the user already saw the banner in the prior session and either dismissed or skipped.)
   - Otherwise calls `UpdateCheckerService.CheckForUpdatesAsync()` with the 15s timeout.
   - Persists `LastCheckUtc = DateTime.UtcNow` regardless of result.
   - If the result has `IsUpdateAvailable == true` and `LatestVersion != SkippedVersion`, marshals to the UI thread and transitions to `Available`.
4. Any exception in `InitializeAsync` is caught, logged, and leaves the banner `Hidden`. Update checks never interrupt the user at launch.

### Banner states

| State | Visible content | Buttons |
|---|---|---|
| `Hidden` | (collapsed) | — |
| `Available` | "gray {version} is available." + release notes preview (first 200 chars) | `Install` · `What's new` · `Later` · `Skip this version` · `✕` |
| `Downloading` | "Downloading {version}… {percent}%" + `ProgressBar` | `Cancel` |
| `ReadyToInstall` | "Installing {version} — gray will restart." | (none) |
| `Error` (previous install failed) | "Last update failed: {message}" | `View log` · `Retry` · `✕` |
| `Error` (session failure) | "Update failed: {message}" | `Retry` · `Open in browser` · `✕` |

### Button handlers

- **Install** — calls `UpdateInstallerService.CanInstall`. If false (multiple windows open, etc.), transitions to `Error` with that reason. Otherwise transitions to `Downloading`, runs `UpdateDownloadService.DownloadAsync` with an `IProgress<double>` that updates the banner text, transitions to `ReadyToInstall`, then calls `UpdateInstallerService.ApplyAndExit`.
- **What's new** — `Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true })`. Banner stays in `Available`.
- **Later** / **✕** — transitions to `Hidden` for this session. Re-prompts on next launch (no persisted time delay).
- **Skip this version** — persists `SkippedVersion = latestVersion` via `SettingsManager`, transitions to `Hidden`. Won't re-prompt until a newer version appears.
- **Cancel** (during download) — cancels the `CancellationTokenSource`, deletes the partial file, transitions back to `Available`.
- **Window closed during download** — `UpdateBannerViewModel.Dispose` (invoked from `MainWindow.Closed`) cancels the CTS and deletes the partial file, same as Cancel. The app exits cleanly without a hanging download task.
- **Retry** (from `Error`) — if the error was from the last-failure file, delete it and transition to `Hidden` (user will see the banner again when a new release lands). If the error was from a mid-session download, transition back to `Available`.
- **View log** (from `Error`) — opens `last-failure.txt` in the default text editor.

### Data flow

```
App.OnLaunched
    └─> MainWindow ctor
            └─> UpdateBannerViewModel.InitializeAsync (background)
                    ├─> SettingsManager.GetUpdatePreferences()
                    ├─> [check last-failure.txt]
                    ├─> [throttle check: LastCheckUtc + 4h]
                    ├─> UpdateCheckerService.CheckForUpdatesAsync → GitHub API
                    └─> DispatcherQueue.TryEnqueue → banner state = Available

User clicks Install
    └─> UpdateBannerViewModel.InstallCommand
            ├─> UpdateInstallerService.CanInstall
            ├─> state = Downloading
            ├─> UpdateDownloadService.DownloadAsync → GitHub asset → MSI file
            │       └─> IProgress<double> → banner percent text
            ├─> state = ReadyToInstall
            └─> UpdateInstallerService.ApplyAndExit
                    ├─> write updater.cmd
                    ├─> Process.Start(updater.cmd) detached
                    └─> Application.Current.Exit()

(gray process exits) → updater.cmd waits → msiexec /passive → upgrade applied → updater.cmd relaunches Gmux.App.exe
```

## PowerShell installer (`install.ps1`)

**Location:** repository root. **Public URL:** `https://raw.githubusercontent.com/Cryptic0011/gray/main/install.ps1`.

**README one-liner:**
```powershell
iwr https://raw.githubusercontent.com/Cryptic0011/gray/main/install.ps1 -UseBasicParsing | iex
```

**Parameters** (usable when downloaded and run directly, ignored when piped):
- `-Version <string>` — pin to a specific release tag (e.g., `v0.2.0`). Default: latest.
- `-KeepInstaller` — don't delete the downloaded MSI after install. Default: delete.
- `-WhatIf` — print the resolved MSI URL and exit without downloading. Used by CI smoke tests.

**Behavior:**
1. Force TLS 1.2: `[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12`.
2. Query `https://api.github.com/repos/Cryptic0011/gray/releases/latest` (or `/releases/tags/{version}` if `-Version` is set) with `User-Agent: gray-installer`.
3. Find the asset where `name -eq 'gray-installer-win-x64.msi'`. Capture `browser_download_url`, `size`, and the release `tag_name`.
4. Download to `$env:TEMP\gray-installer-<tag>.msi` via `Invoke-WebRequest` (shows built-in progress bar).
5. `Unblock-File` the downloaded MSI to strip Zone.Identifier.
6. Run `Start-Process msiexec.exe -ArgumentList '/i', '<path>', '/passive', '/norestart', '/l*v', '<logpath>' -Wait -PassThru` and capture `ExitCode`.
7. On success (`ExitCode -eq 0`): print `gray <tag> installed. Run 'gray' or launch from Start menu.` and exit 0. Delete MSI unless `-KeepInstaller`.
8. On failure: print the last 20 lines of the log file, print `"Install failed. Full log: <path>"`, exit with `ExitCode`.

**Error handling:**
- `iwr` throws on network error → catch, print `"Couldn't reach GitHub. Check your connection."`, exit 1.
- GitHub returns 403 rate-limited → detect via status code, print `"GitHub rate limit exceeded. Try again later or download the MSI from: <release URL>"`, exit 1.
- `-Version` specified but not found → `iwr` returns 404, print `"Version <version> not found. See https://github.com/Cryptic0011/gray/releases"`, exit 1.
- Asset missing from release → print `"No installer asset in release <tag>"`, exit 1.

**What it deliberately does NOT do:**
- No execution-policy workaround (`iex` bypasses policy by design).
- No elevation (`#Requires -RunAsAdministrator`). MSI is per-user, no admin needed.
- No `-Uninstall` flag. MSI registers with Add/Remove Programs; uninstall is done there. Follow-up if requested.
- No hash verification. Follow-up alongside the in-app updater.
- No portable-zip fallback. MSI only.

**README addition** near the top:

```markdown
## Install

**Windows PowerShell one-liner:**

    iwr https://raw.githubusercontent.com/Cryptic0011/gray/main/install.ps1 -UseBasicParsing | iex

Or download the MSI directly from the [latest release](https://github.com/Cryptic0011/gray/releases/latest).

If Windows SmartScreen warns, click **More info → Run anyway**. (Code signing is planned.)
```

## Error handling

### Update check failures (launch-time, silent)
These never surface to the user — the banner stays `Hidden` and the error is logged.
- Timeout (15s) → logged as `"Update check timed out"`.
- `403` with rate limit → logged; `LastCheckUtc` updated to enforce 4h throttle.
- Any `4xx`/`5xx` or malformed JSON → logged.
- DNS failure → logged.

### Download failures (user-initiated, visible)
These transition the banner to `Error` with actionable retry/open-in-browser buttons.
- Timeout mid-download → `"Download timed out."`
- `404` on asset URL → `"Installer asset not found for {version}."`
- Disk full / IO error → `"Couldn't write to {path}: {message}."`
- User cancel → back to `Available`, no error surfaced, partial file deleted.

### Installer failures
The `updater.cmd` batch script copies the install log to `last-failure.txt` on non-zero `msiexec` exit. On next gray launch, `UpdateBannerViewModel.InitializeAsync` checks for that file and shows the `Error` state with a **View log** button. The file is deleted when the user dismisses the error.

### Multi-window detection
Before calling `ApplyAndExit`, `UpdateInstallerService.CanInstall` runs `Process.GetProcessesByName("Gmux.App")`. If more than one, `CanInstall` returns false with reason `"Close other gray windows first, then try again."`. The banner shows this as an `Error` with `Retry` (re-checks) and `✕` (dismiss).

### Version comparison edge cases
The fixed `NormalizeVersion` handles `v0.2.0`, `0.2.0`, `V0.2.0`, `0.2.0.0`, `0.2.0+abc123`, `0.2.0-beta.1`, unparseable strings, and null. `UpdateCheckerService` rejects prerelease tags (containing `-`) when the current version is stable — stable users never get pushed to a beta. Full semver comparison (including prerelease ordering) is a follow-up.

### Concurrency
Only one `UpdateBannerViewModel` per process. Multi-window scenarios reuse the same VM. Two gray processes won't simultaneously download because multi-window `CanInstall` refuses.

## Testing

### New test project

Add `src/Gmux.Core.Tests/Gmux.Core.Tests.csproj` using xUnit and `Microsoft.NET.Test.Sdk`. Reference from `gmux.sln`. Tests run in CI via `dotnet test` added to `.github/workflows/ci.yml`.

### Unit tests

**`NormalizeVersionTests`** — `[Theory]` with inputs:
- `"v0.2.0"` → `0.2.0`
- `"0.2.0"` → `0.2.0`
- `"V0.2.0"` → `0.2.0`
- `"0.2.0.0"` → `0.2.0.0`
- `"0.2.0+abc123"` → `0.2.0`
- `"0.2.0-beta.1"` → `0.2.0`
- `"garbage"` → `0.0.0.0`
- `""` → `0.0.0.0`
- `null` → `0.0.0.0`

**`TryParseGitHubRepoTests`** — covers:
- `"https://github.com/Cryptic0011/gray"` → `(Cryptic0011, gray)`, returns true.
- `"https://github.com/Cryptic0011/gray.git"` → `(Cryptic0011, gray)`, returns true.
- `"https://github.com/Cryptic0011/gray/"` → same.
- `"https://gitlab.com/foo/bar"` → returns false.
- `""`, `null`, `"not a url"` → returns false.

**`UpdateCheckerServiceTests`** — uses a fake `HttpMessageHandler` (implement a `TestHttpMessageHandler : HttpMessageHandler` that returns canned responses). Covers:
- Newer `tag_name` → `IsUpdateAvailable == true`, `MsiAssetUrl` populated from matching asset.
- Equal `tag_name` → `IsUpdateAvailable == false`.
- Prerelease tag (`"v0.2.0-beta.1"`) on stable current → `IsUpdateAvailable == false`.
- `403` with rate limit body → result populated with `"rate limit"` in Message, no exception.
- `500` → result populated with status in Message, no exception.
- Asset list missing the MSI → `MsiAssetUrl == null`, `IsUpdateAvailable == false`.
- Network timeout (handler throws `TaskCanceledException`) → result populated, no exception escapes.

**`UpdateDownloadServiceTests`** — fake handler that streams a known byte sequence with small delays. Covers:
- Happy path: progress fires monotonically from 0 to 1, file contents match input, returns correct path.
- Cancellation via `CancellationToken` → partial file deleted, `OperationCanceledException` propagates.
- `404` from handler → throws `UpdateDownloadException` with asset URL in message.
- Write to an unwritable path → throws `UpdateDownloadException`, no partial file leaked.

**`UpdateBannerViewModelTests`** — extract thin interfaces (`IUpdateCheckerService`, `IUpdateDownloadService`, `IUpdateInstallerService`, `ISettingsManager`) purely for testability. Covers:
- Init with `SkippedVersion == latestVersion` → stays `Hidden`.
- Init with `LastCheckUtc` within 4h and cached "no update" → no network call, stays `Hidden`.
- Init with update available → state `Available`.
- Install → `Downloading` → `ReadyToInstall` → calls `ApplyAndExit`.
- Install when `CanInstall` returns false → `Error` state with reason.
- Download cancelled → back to `Available`, no error.
- Download fails → `Error` state; Retry transitions back to `Available`.
- Skip this version → `SkippedVersion` persisted, state `Hidden`.

### CI changes

`.github/workflows/ci.yml`:
- Add `dotnet test src/Gmux.Core.Tests/Gmux.Core.Tests.csproj --configuration Release --no-build` after the build step.
- Install PSScriptAnalyzer: `Install-Module -Name PSScriptAnalyzer -Force -Scope CurrentUser`.
- Run `Invoke-ScriptAnalyzer -Path install.ps1 -Severity Error,Warning` — fail on any findings.
- Run `pwsh -File install.ps1 -Version v0.1.0 -WhatIf` as a smoke test — verifies the script resolves the correct asset URL without actually installing.

### Manual smoke test (documented for the release engineer)

End-to-end happy path requires real `msiexec` and a real release, which can't run in CI. Document in the spec:

1. Build v0.1.0 locally, install via MSI.
2. On a throwaway tag (e.g., `v0.1.1-smoketest` on a fork), push a release that passes through the fixed `release.yml`.
3. Launch installed v0.1.0 → wait for banner to appear → click **Install** → watch download progress → app exits → `updater.cmd` runs MSI silently → app relaunches → verify sidebar "About" shows v0.1.1.
4. Repeat, click **Skip this version** → verify no banner on next launch.
5. Repeat, click **Later** → verify banner reappears on next launch.
6. Force a download failure (disconnect network mid-download) → verify `Error` state with **Retry**.
7. Open two gray windows → click **Install** → verify `Error` state with "Close other gray windows first."
8. From a fresh Windows install (or clean VM), run the PowerShell one-liner → verify it installs → verify gray launches from Start menu.

### What's not tested in v1
- The `updater.cmd` batch script (covered by manual smoke test).
- Actual `msiexec /passive` invocation (covered by manual smoke test).
- Real GitHub API (fake `HttpMessageHandler` is sufficient).
- Cross-version matrix (only the latest released version is smoke-tested per release).

## Rollout

1. Merge the bug fixes (release workflow, `NormalizeVersion`, `TryParseGitHubRepo`) in a small PR first. These are independently valuable and unblock everything else.
2. Merge the new services, ViewModel, banner control, and test project in a second PR. Feature-complete but unused.
3. Wire the banner into `MainWindow` in a third PR. This is the user-visible "feature on" moment.
4. Add `install.ps1` and the README update in a fourth PR.
5. Cut v0.2.0 release. Smoke test manually against v0.1.0 installed from the current MSI.

## Follow-ups (explicitly out of scope)

- **Code signing** the MSI (to eliminate SmartScreen warnings for users downloading manually from GitHub).
- **SHA256 verification** of downloaded assets. Requires adding a `sha256sum.txt` asset to the release workflow.
- **Full semver comparison** including prerelease ordering (`0.2.0-beta.1 < 0.2.0-beta.2 < 0.2.0`).
- **Opt-in beta channel** (users could flip a setting to receive prerelease updates).
- **Background silent updates** without the confirmation prompt (would require a tray presence or startup hook — separate design).
- **`install.ps1 -Uninstall`** flag.
- **Delta updates** via Velopack or similar. Only worth it if the MSI grows past ~50 MB.
- **Telemetry** for update success/failure rates.

## Open questions — none

All design decisions were resolved during brainstorming.
