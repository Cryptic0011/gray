# MSI Installer + GitHub Release Automation

**Date:** 2026-04-06
**Status:** Approved

## Overview

Add a WiX v4 MSI installer for gray and update the GitHub Actions release workflow to build and publish the MSI alongside the existing portable zip archives. Triggered by version tags (`v*`).

## Scope

**In scope:**
- WiX v4 MSI installer project (`src/Gmux.Setup/`)
- Per-machine and per-user install support
- Desktop shortcut
- PATH registration for CLI
- Updated `release.yml` to build MSI and attach to GitHub Releases
- Tag-triggered releases only

**Out of scope:**
- ARM64 builds (x64 only for now)
- winget/scoop manifests
- MSIX packaging
- Start menu shortcut
- Auto-start

## WiX v4 MSI Installer

### Project Structure

```
src/Gmux.Setup/
  Gmux.Setup.wixproj     WiX v4 SDK-style project
  Product.wxs             Main installer definition (features, components, UI)
  Variables.wxi           Shared version/name constants
```

The project is added to `gmux.sln` but excluded from the default build configuration (built explicitly during CI via `dotnet build` with the Release configuration). This avoids slowing down local dev builds.

### Install Behavior

| Aspect | Detail |
|--------|--------|
| **Install scope** | User chooses: per-machine (`C:\Program Files\gray\`) or per-user (`%LocalAppData%\Programs\gray\`) |
| **Contents** | All published files from Gmux.App + Gmux.Cli |
| **Desktop shortcut** | `gray.lnk` pointing to `Gmux.App.exe`, using `ico.ico` |
| **PATH** | Adds install directory to system PATH (per-machine) or user PATH (per-user) so `gray` CLI is available from any terminal |
| **Uninstall** | Standard Add/Remove Programs entry; removes all installed files, shortcut, and PATH entry |
| **Upgrade** | Major upgrade strategy via a stable UpgradeCode GUID. New versions cleanly replace old ones (no side-by-side). |

### Version Source

The MSI version is derived from `Directory.Build.props` (`0.1.0`). The WiX project reads this via a `Variables.wxi` include that defines `ProductVersion`. The CI workflow passes the version as an MSBuild property so there is a single source of truth.

### Scope Selection UI

WiX provides a `WixUI_InstallDir` dialog set. A custom dialog step or property-driven approach lets the user pick per-machine vs per-user:

- Per-machine: sets `ALLUSERS=1`, installs to `ProgramFiles64Folder\gray`
- Per-user: sets `ALLUSERS=""`, installs to `LocalAppDataFolder\Programs\gray`

The install directory is not user-customizable beyond this choice (keeps it simple).

### Component Layout

Two feature groups:

1. **App** (required) — Gmux.App.exe and all runtime dependencies
2. **CLI** (required) — Gmux.Cli.exe (gray.exe CLI)

Both always install. No optional features. The PATH environment variable component uses the `Environment` WiX element with `Action="set"` and `Part="last"`.

## GitHub Actions Release Workflow

### Trigger

```yaml
on:
  push:
    tags:
      - 'v*'
```

Tags only. No drafts on main. PRs continue to run build-only validation (no publish/release steps).

### Pipeline Steps

```
1. Checkout repo
2. Setup .NET 8.0.x
3. Restore packages
4. Build solution (Release, x64)
5. Publish Gmux.App  → artifacts/app/   (win-x64, framework-dependent)
6. Publish Gmux.Cli  → artifacts/cli/   (win-x64, framework-dependent)
7. Install WiX v4 toolset (dotnet tool)
8. Build Gmux.Setup   → artifacts/setup/ (produces gray-installer-win-x64.msi)
9. Zip app + cli → gray-app-win-x64.zip, gray-cli-win-x64.zip
10. Create GitHub Release from tag
    - Title: tag name (e.g. "v0.1.0")
    - Assets: gray-installer-win-x64.msi, gray-app-win-x64.zip, gray-cli-win-x64.zip
    - Auto-generate release notes from commits
```

### WiX Toolset in CI

WiX v4 is distributed as a .NET tool. The workflow installs it with:

```yaml
- run: dotnet tool install --global wix
```

The `Gmux.Setup.wixproj` uses the `WixToolset.Sdk` MSBuild SDK, so `dotnet build` drives the MSI compilation.

### Artifact Naming

| Asset | Filename |
|-------|----------|
| MSI installer | `gray-installer-win-x64.msi` |
| App portable zip | `gray-app-win-x64.zip` |
| CLI portable zip | `gray-cli-win-x64.zip` |

### Version Flow

1. Developer bumps version in `Directory.Build.props`
2. Commits, tags with matching `v{version}` (e.g. `v0.2.0`)
3. Pushes tag: `git push origin v0.2.0`
4. CI builds everything, creates GitHub Release
5. `UpdateCheckerService` in the app compares its embedded version against the latest release tag

## Existing Infrastructure (No Changes)

- **UpdateCheckerService**: Already checks `api.github.com/repos/{owner}/{repo}/releases/latest` on app launch and compares tag against embedded version. No changes needed.
- **Directory.Build.props**: Already defines version `0.1.0`. Single source of truth.
- **Portable zip workflow**: Already works. MSI is additive.

## Testing

- Build MSI locally with `dotnet build src/Gmux.Setup/Gmux.Setup.csproj -c Release`
- Install on a clean Windows machine (or VM) — verify:
  - Per-machine and per-user paths work
  - Desktop shortcut launches the app
  - `gray` CLI accessible from a new terminal (PATH works)
  - Uninstall removes files, shortcut, and PATH entry
  - Upgrade over previous version replaces cleanly
- Tag a test release on a fork to validate CI end-to-end
