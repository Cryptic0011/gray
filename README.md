# gray

Windows-first terminal multiplexer for agent CLIs such as Claude, Codex, and Gemini.

## Current state

The app supports:
- workspaces, tabs, and split panes
- configurable agent launchers for Claude / Codex / Gemini
- waiting-state notifications for tracked agent panes
- persisted settings and workspace state
- GitHub release update checks

## Install

### Option 1: Build from source

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

### Option 2: Portable release

Download the latest release zip from GitHub Releases, extract it, and run `Gmux.App.exe`.

### Option 3: Installer

MSI packaging is the intended release path, but the repo does not yet include MSI authoring. Use GitHub Releases portable assets or build from source until installer packaging is added.

### Option 4: winget

`winget` packaging is planned. The workflow in this repo prepares release artifacts, but no published winget manifest is included yet.

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
4. Add installer packaging if you want MSI distribution.
5. Add a winget manifest if you want command-line installation.

## Release automation

This repo includes a GitHub Actions workflow that:
- builds the solution
- publishes app and CLI artifacts
- uploads them as workflow artifacts
- creates a GitHub Release and attaches zip assets on version tags

Tag a release:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

## Update checks

The app checks GitHub Releases using the `RepositoryUrl` assembly metadata. If `RepositoryUrl` is left as the placeholder value, update checks will report that releases are not configured.
