# gray

Windows-first terminal multiplexer for agent CLIs such as Claude, Codex, and Gemini.

## Features

gray supports:
- workspaces, tabs, and split panes
- configurable agent launchers for Claude / Codex / Gemini
- waiting-state notifications for tracked agent panes
- persisted settings and workspace state
- GitHub release update checks with in-app updates

## Install

gray currently targets Windows 10/11.

### Recommended: PowerShell one-liner

```powershell
iwr https://raw.githubusercontent.com/Cryptic0011/gray/main/install.ps1 -UseBasicParsing | iex
```

This installs the latest GitHub release to `%LocalAppData%\Programs\gray`, adds `gray` to your user `PATH`, and creates a desktop shortcut.

After install:
- launch `gray` from the Start menu, desktop shortcut, or a new terminal
- gray checks GitHub for updates on launch
- updates install through the same MSI from an in-app banner

### Manual install: MSI

Download `gray-installer-win-x64.msi` from the [latest release](https://github.com/Cryptic0011/gray/releases/latest) and run it.

The installer:
- installs `gray` to `%LocalAppData%\Programs\gray`
- creates a desktop shortcut
- adds the install directory to user `PATH` so `gray` is available from a new shell

### Portable downloads

Download `gray-app-win-x64.zip` from GitHub Releases, extract it, and run `Gmux.App.exe`.

If you only want the CLI without installing the full app, download `gray-cli-win-x64.zip`.

### Notes

- If Windows SmartScreen warns about the installer, click **More info -> Run anyway**.
- `winget` packaging is prepared in this repo but is not published yet, so `winget install gray` is not available yet.

## Build From Source

Requirements:
- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022 or Build Tools with Windows App SDK support

Build:

```powershell
git clone https://github.com/Cryptic0011/gray.git
cd gray
dotnet build gmux.sln
```

Run the app:

```powershell
dotnet run --project src/Gmux.App/Gmux.App.csproj
```

Run the CLI:

```powershell
dotnet run --project src/Gmux.Cli/Gmux.Cli.csproj -- status
```

## Settings

Open `Settings` from the sidebar to configure:
- enabled agent CLIs
- default shell
- preferred agent / chooser mode / disabled auto-launch
- custom launch commands
- waiting notification scope and duration
- terminal font size
- scrollback size

The settings dialog also shows:
- current app version
- shell / agent command availability
- manual GitHub update check

## Publishing checklist

Before publishing to GitHub:

1. Update `RepositoryUrl` in [Directory.Build.props](Directory.Build.props).
2. Set the correct version in [Directory.Build.props](Directory.Build.props).
3. Tag releases with semantic versions like `v0.1.0`.
4. Push the tag to GitHub.
5. Refresh `packaging/winget/manifests/` from the release MSI and validate it with `winget validate`.
6. Submit the manifest to `microsoft/winget-pkgs` if you want `winget install gray`.

## Winget Packaging

This repo includes a `winget` manifest generator under `packaging/winget/`.

Generate or refresh manifests from the current MSI:

```powershell
pwsh -File packaging/winget/New-WingetManifest.ps1
```

Validate the generated manifest:

```powershell
winget validate --manifest packaging/winget/manifests/c/Cryptic0011/gray/0.1.0
```

The package is not published to the community `winget-pkgs` repo yet.

## Release automation

This repo includes a GitHub Actions workflow that:
- builds the solution
- publishes app and CLI artifacts
- builds `gray-installer-win-x64.msi` with WiX
- uploads them as workflow artifacts
- creates a GitHub Release on version tags
- attaches:
  - `gray-installer-win-x64.msi`
  - `gray-app-win-x64.zip`
  - `gray-cli-win-x64.zip`

Tag a release:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Build the installer locally:

```powershell
dotnet publish src/Gmux.App/Gmux.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/app
dotnet publish src/Gmux.Cli/Gmux.Cli.csproj -c Release -r win-x64 --self-contained false -o artifacts/cli
dotnet build src/Gmux.Setup/Gmux.Setup.wixproj -c Release -p:ProductVersion=0.1.0
```

The MSI is written to `src/Gmux.Setup/bin/Release/gray-installer-win-x64.msi`.

## Update checks

The app checks GitHub Releases using the `RepositoryUrl` assembly metadata. If `RepositoryUrl` is left as the placeholder value, update checks will report that releases are not configured.
