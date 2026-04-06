# MSI Installer + GitHub Release Automation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a WiX v4 MSI installer for gray and update the GitHub Actions workflow to build and publish it on version tags.

**Architecture:** A new `src/Gmux.Setup/` WiX v4 project harvests published App + CLI files into an MSI with per-machine/per-user scope selection, desktop shortcut, and PATH registration. The CI workflow is updated to build the MSI and attach it to GitHub Releases alongside the existing zip archives.

**Tech Stack:** WiX v4 (WixToolset.Sdk), MSBuild, GitHub Actions, PowerShell

---

## File Map

| Action | File | Purpose |
|--------|------|---------|
| Create | `src/Gmux.Setup/Gmux.Setup.wixproj` | WiX v4 SDK project file |
| Create | `src/Gmux.Setup/Variables.wxi` | Shared version/name/GUID constants |
| Create | `src/Gmux.Setup/Product.wxs` | Main installer definition: features, components, UI, shortcuts, PATH |
| Modify | `gmux.sln` | Add Gmux.Setup project (build disabled in default config) |
| Modify | `.github/workflows/release.yml` | Add MSI build step and attach to releases |

---

### Task 1: Create the WiX v4 Project File

**Files:**
- Create: `src/Gmux.Setup/Gmux.Setup.wixproj`

This is the MSBuild project that uses the WiX v4 SDK. It references the published App and CLI output directories. It does NOT reference the other .csproj files — it operates on already-published output.

- [ ] **Step 1: Create `src/Gmux.Setup/Gmux.Setup.wixproj`**

```xml
<Project Sdk="WixToolset.Sdk/4.0.7">

  <PropertyGroup>
    <OutputType>Package</OutputType>
    <InstallerPlatform>x64</InstallerPlatform>
    <OutputName>gray-installer-win-x64</OutputName>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: Commit**

```bash
git add src/Gmux.Setup/Gmux.Setup.wixproj
git commit -m "feat(setup): add WiX v4 project file"
```

---

### Task 2: Create Variables Include

**Files:**
- Create: `src/Gmux.Setup/Variables.wxi`

Centralizes the product name, version, manufacturer, and UpgradeCode GUID. The version is passed in from MSBuild (CI sets it from `Directory.Build.props`), with a fallback default.

- [ ] **Step 1: Create `src/Gmux.Setup/Variables.wxi`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Include>
  <?ifndef ProductVersion ?>
    <?define ProductVersion = "0.1.0" ?>
  <?endif ?>

  <?define ProductName = "gray" ?>
  <?define Manufacturer = "gray contributors" ?>
  <?define UpgradeCode = "7A2E4F91-3B8C-4D5E-A6F0-1C9D2E8B7A43" ?>
  <?define AppPublishDir = "..\..\artifacts\app" ?>
  <?define CliPublishDir = "..\..\artifacts\cli" ?>
</Include>
```

The `UpgradeCode` GUID is stable across all versions — this is what Windows uses to detect existing installations for upgrade. Never change it.

- [ ] **Step 2: Commit**

```bash
git add src/Gmux.Setup/Variables.wxi
git commit -m "feat(setup): add WiX variables include with version and GUIDs"
```

---

### Task 3: Create Product.wxs — Core Installer Definition

**Files:**
- Create: `src/Gmux.Setup/Product.wxs`

This is the main WiX source. It defines:
- The MSI package with per-machine/per-user scope selection
- File harvesting from the published App and CLI directories
- A desktop shortcut to `Gmux.App.exe`
- PATH environment variable registration
- Major upgrade strategy (new version replaces old)
- Minimal WixUI dialog set for install scope and progress

- [ ] **Step 1: Create `src/Gmux.Setup/Product.wxs`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"
     xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">

  <?include Variables.wxi ?>

  <Package Name="$(ProductName)"
           Version="$(ProductVersion)"
           Manufacturer="$(Manufacturer)"
           UpgradeCode="$(UpgradeCode)"
           Scope="perMachineOrPerUser"
           InstallerVersion="500"
           Compressed="yes">

    <MajorUpgrade DowngradeErrorMessage="A newer version of $(ProductName) is already installed." />
    <MediaTemplate EmbedCab="yes" />

    <!-- Per-machine: Program Files\gray, Per-user: LocalAppData\Programs\gray -->
    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER_MACHINE" Name="gray" />
    </StandardDirectory>

    <StandardDirectory Id="LocalAppDataFolder">
      <Directory Id="ProgramsSubfolder" Name="Programs">
        <Directory Id="INSTALLFOLDER_USER" Name="gray" />
      </Directory>
    </StandardDirectory>

    <StandardDirectory Id="DesktopFolder" />

    <!-- App files harvested from publish output -->
    <ComponentGroup Id="AppFiles" Directory="INSTALLFOLDER">
      <Files Include="$(AppPublishDir)\**"
             Exclude="$(AppPublishDir)\**\*.pdb" />
    </ComponentGroup>

    <!-- CLI files (only Gmux.Cli-specific files; shared DLLs already in App) -->
    <ComponentGroup Id="CliFiles" Directory="INSTALLFOLDER">
      <Files Include="$(CliPublishDir)\Gmux.Cli.*" />
      <Files Include="$(CliPublishDir)\System.CommandLine.dll" />
    </ComponentGroup>

    <!-- Desktop shortcut -->
    <Component Id="DesktopShortcutComponent" Directory="DesktopFolder" Guid="B3F1A2D4-5E6C-7890-AB12-CD34EF56A789">
      <Shortcut Id="DesktopShortcut"
                Name="gray"
                Description="Terminal multiplexer for AI agents"
                Target="[INSTALLFOLDER]Gmux.App.exe"
                WorkingDirectory="INSTALLFOLDER"
                Icon="AppIcon" />
      <RegistryValue Root="HKCU"
                     Key="Software\gray\Install"
                     Name="DesktopShortcut"
                     Type="integer"
                     Value="1"
                     KeyPath="yes" />
    </Component>

    <!-- Add install dir to PATH -->
    <Component Id="PathEnvComponent" Directory="INSTALLFOLDER" Guid="C4E2B3A1-6D7F-8901-BC23-DE45FA67B890">
      <Environment Id="PathEntry"
                   Name="PATH"
                   Value="[INSTALLFOLDER]"
                   Permanent="no"
                   Part="last"
                   Action="set"
                   System="yes" />
      <RegistryValue Root="HKCU"
                     Key="Software\gray\Install"
                     Name="PathEnv"
                     Type="integer"
                     Value="1"
                     KeyPath="yes" />
    </Component>

    <Feature Id="Complete" Title="gray" Level="1">
      <ComponentGroupRef Id="AppFiles" />
      <ComponentGroupRef Id="CliFiles" />
      <ComponentRef Id="DesktopShortcutComponent" />
      <ComponentRef Id="PathEnvComponent" />
    </Feature>

    <!-- App icon for shortcuts and Add/Remove Programs -->
    <Icon Id="AppIcon" SourceFile="$(AppPublishDir)\ico.ico" />
    <Property Id="ARPPRODUCTICON" Value="AppIcon" />

    <!-- Minimal UI: scope selection + progress -->
    <Property Id="WIXUI_INSTALLDIR" Value="INSTALLFOLDER" />
    <ui:WixUI Id="WixUI_InstallDir" />

    <!-- Resolve INSTALLFOLDER based on scope -->
    <SetDirectory Id="INSTALLFOLDER" Value="[INSTALLFOLDER_MACHINE]" Condition="ALLUSERS = 1" Sequence="first" />
    <SetDirectory Id="INSTALLFOLDER" Value="[INSTALLFOLDER_USER]" Condition="NOT (ALLUSERS = 1)" Sequence="first" />

  </Package>

</Wix>
```

- [ ] **Step 2: Verify the WiX project builds locally**

First, install the WiX .NET tool if not already installed:

```bash
dotnet tool install --global wix
```

Then publish the App and CLI (required as input for the MSI build):

```bash
dotnet publish src/Gmux.App/Gmux.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/app
dotnet publish src/Gmux.Cli/Gmux.Cli.csproj -c Release -r win-x64 --self-contained false -o artifacts/cli
```

Then build the MSI:

```bash
dotnet build src/Gmux.Setup/Gmux.Setup.wixproj -c Release -p:ProductVersion=0.1.0
```

Expected: Build succeeds and produces `src/Gmux.Setup/bin/Release/gray-installer-win-x64.msi`

- [ ] **Step 3: Commit**

```bash
git add src/Gmux.Setup/Product.wxs
git commit -m "feat(setup): add main installer definition with shortcut and PATH"
```

---

### Task 4: Add Gmux.Setup to Solution (Build Disabled by Default)

**Files:**
- Modify: `gmux.sln`

The Setup project is added to the solution so it's visible in Visual Studio, but its build is disabled in all configurations. It's only built explicitly by CI (or manually via `dotnet build`). This avoids slowing local dev builds and avoids needing WiX installed locally for regular development.

- [ ] **Step 1: Add the project to the solution**

Run from the repo root:

```bash
dotnet sln gmux.sln add src/Gmux.Setup/Gmux.Setup.wixproj --solution-folder src
```

- [ ] **Step 2: Disable build in all configurations**

Open `gmux.sln` and find the newly added `Gmux.Setup` project configuration entries. For every configuration line that ends with `.Build.0`, remove that line. This keeps the project in the solution for visibility but prevents it from building during `dotnet build gmux.sln`.

The entries to **remove** look like:

```
{GUID}.Debug|x64.Build.0 = ...
{GUID}.Release|x64.Build.0 = ...
{GUID}.Debug|ARM64.Build.0 = ...
{GUID}.Release|ARM64.Build.0 = ...
```

Keep the `.ActiveCfg` lines — only remove `.Build.0` lines for the Setup project GUID.

- [ ] **Step 3: Verify solution builds without WiX**

```bash
dotnet build gmux.sln -c Release
```

Expected: Build succeeds. The Setup project is skipped (no `.Build.0` entries). No WiX tooling required.

- [ ] **Step 4: Commit**

```bash
git add gmux.sln
git commit -m "feat(setup): add Gmux.Setup to solution (build disabled by default)"
```

---

### Task 5: Update GitHub Actions Workflow

**Files:**
- Modify: `.github/workflows/release.yml`

Update the workflow to:
- Only trigger on version tags (remove `push: branches: [main]` and `pull_request`)
- Install the WiX v4 .NET tool
- Build the MSI after publishing App and CLI
- Copy the MSI to `dist/` alongside the zips
- Attach the MSI to the GitHub Release

- [ ] **Step 1: Replace `.github/workflows/release.yml` with updated workflow**

```yaml
name: Build And Release

on:
  push:
    tags: [ 'v*' ]

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

      - name: Extract version from tag
        id: version
        shell: pwsh
        run: |
          $tag = "${{ github.ref_name }}"
          $ver = $tag -replace '^v', ''
          echo "VERSION=$ver" >> $env:GITHUB_OUTPUT

      - name: Restore
        run: dotnet restore gmux.sln

      - name: Build
        run: dotnet build gmux.sln --configuration Release --no-restore

      - name: Publish App
        run: dotnet publish src/Gmux.App/Gmux.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/app

      - name: Publish CLI
        run: dotnet publish src/Gmux.Cli/Gmux.Cli.csproj -c Release -r win-x64 --self-contained false -o artifacts/cli

      - name: Install WiX Toolset
        run: dotnet tool install --global wix

      - name: Build MSI Installer
        run: dotnet build src/Gmux.Setup/Gmux.Setup.wixproj -c Release -p:ProductVersion=${{ steps.version.outputs.VERSION }}

      - name: Stage Release Assets
        shell: pwsh
        run: |
          New-Item -ItemType Directory -Force -Path dist | Out-Null
          Compress-Archive -Path artifacts/app/* -DestinationPath dist/gray-app-win-x64.zip -Force
          Compress-Archive -Path artifacts/cli/* -DestinationPath dist/gray-cli-win-x64.zip -Force
          Copy-Item src/Gmux.Setup/bin/Release/gray-installer-win-x64.msi dist/

      - name: Upload Artifacts
        uses: actions/upload-artifact@v4
        with:
          name: gray-release-artifacts
          path: dist/*

      - name: Create Release
        uses: softprops/action-gh-release@v2
        with:
          generate_release_notes: true
          files: |
            dist/gray-installer-win-x64.msi
            dist/gray-app-win-x64.zip
            dist/gray-cli-win-x64.zip
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "feat(ci): build MSI and attach to GitHub Releases on version tags"
```

---

### Task 6: Add CI Build Validation Workflow for PRs

**Files:**
- Create: `.github/workflows/ci.yml`

The old `release.yml` ran on PRs and main pushes for build validation. Since we changed it to tags-only, we need a separate lightweight workflow for CI on PRs and main pushes (build only, no publish/release).

- [ ] **Step 1: Create `.github/workflows/ci.yml`**

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
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "feat(ci): add build validation workflow for PRs and main pushes"
```

---

### Task 7: Local Smoke Test

No new files. Verify the full pipeline works locally before pushing.

- [ ] **Step 1: Clean previous build artifacts**

```bash
rm -rf artifacts dist src/Gmux.Setup/bin src/Gmux.Setup/obj
```

- [ ] **Step 2: Publish App and CLI**

```bash
dotnet publish src/Gmux.App/Gmux.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/app
dotnet publish src/Gmux.Cli/Gmux.Cli.csproj -c Release -r win-x64 --self-contained false -o artifacts/cli
```

Expected: Both publish successfully to `artifacts/app` and `artifacts/cli`.

- [ ] **Step 3: Install WiX tool and build MSI**

```bash
dotnet tool install --global wix 2>/dev/null; dotnet build src/Gmux.Setup/Gmux.Setup.wixproj -c Release -p:ProductVersion=0.1.0
```

Expected: MSI produced at `src/Gmux.Setup/bin/Release/gray-installer-win-x64.msi`.

- [ ] **Step 4: Verify the MSI was produced**

```bash
ls -lh src/Gmux.Setup/bin/Release/gray-installer-win-x64.msi
```

Expected: File exists, size roughly 8-15 MB.

- [ ] **Step 5: (Optional) Test install on your machine**

Run the MSI. Verify:
- Scope selection dialog appears (per-machine vs per-user)
- After install, `gray` desktop shortcut exists and launches the app
- Open a new terminal: `where gray` or `where Gmux.Cli` finds the CLI in PATH
- Uninstall via Add/Remove Programs removes files, shortcut, and PATH entry
