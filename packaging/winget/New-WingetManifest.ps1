#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$MsiPath = '',
    [string]$OutputRoot = '',
    [string]$PackageIdentifier = 'Cryptic0011.gray',
    [string]$Publisher = 'Cryptic0011',
    [string]$PackageName = 'gray',
    [string]$PackageUrl = 'https://github.com/Cryptic0011/gray',
    [string]$PublisherUrl = 'https://github.com/Cryptic0011',
    [string]$License = 'Proprietary',
    [string]$ShortDescription = 'Windows-first terminal multiplexer for agent CLIs such as Claude, Codex, and Gemini.',
    [string]$ManifestVersion = '1.12.0'
)

$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

function Get-VersionFromProps([string]$repoRoot) {
    [xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
    return [string]$props.Project.PropertyGroup.Version
}

function Get-MsiProductCode([string]$resolvedMsiPath) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($resolvedMsiPath, 0))
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT Value FROM Property WHERE Property='ProductCode'"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if (-not $record) {
        throw "Could not read ProductCode from $resolvedMsiPath"
    }

    return [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
}

function Set-Utf8File([string]$path, [string]$content) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($path, $content, $utf8NoBom)
}

$repoRoot = Get-RepoRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-VersionFromProps $repoRoot
}

if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $MsiPath = Join-Path $repoRoot 'dist\gray-installer-win-x64.msi'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'packaging\winget\manifests'
}

$resolvedMsiPath = (Resolve-Path $MsiPath).Path
$packageParts = $PackageIdentifier.Split('.', 2)
if ($packageParts.Length -ne 2) {
    throw "PackageIdentifier must be in Publisher.Package format."
}

$manifestDir = Join-Path $OutputRoot (Join-Path $packageParts[0].Substring(0, 1).ToLowerInvariant() (Join-Path $packageParts[0] (Join-Path $packageParts[1] $Version)))
New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null

$sha256 = (Get-FileHash $resolvedMsiPath -Algorithm SHA256).Hash.ToUpperInvariant()
$productCode = Get-MsiProductCode $resolvedMsiPath
$installerUrl = "https://github.com/Cryptic0011/gray/releases/download/v$Version/gray-installer-win-x64.msi"
$tags = @('terminal', 'multiplexer', 'ai', 'claude', 'codex', 'gemini')
$versionSchema = "https://aka.ms/winget-manifest.version.$ManifestVersion.schema.json"
$localeSchema = "https://aka.ms/winget-manifest.defaultLocale.$ManifestVersion.schema.json"
$installerSchema = "https://aka.ms/winget-manifest.installer.$ManifestVersion.schema.json"

$versionManifest = @"
# yaml-language-server: `$schema=$versionSchema
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $ManifestVersion
"@

$localeManifest = @"
# yaml-language-server: `$schema=$localeSchema
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: $Publisher
PublisherUrl: $PublisherUrl
PackageName: $PackageName
PackageUrl: $PackageUrl
License: $License
ShortDescription: $ShortDescription
Tags:
$(
    ($tags | ForEach-Object { "- $_" }) -join "`n"
)
ManifestType: defaultLocale
ManifestVersion: $ManifestVersion
"@

$installerManifest = @"
# yaml-language-server: `$schema=$installerSchema
PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
Installers:
  - Architecture: x64
    InstallerType: msi
    Scope: user
    InstallerUrl: $installerUrl
    InstallerSha256: $sha256
    ProductCode: '$productCode'
ManifestType: installer
ManifestVersion: $ManifestVersion
"@

Set-Utf8File (Join-Path $manifestDir "$PackageIdentifier.yaml") $versionManifest
Set-Utf8File (Join-Path $manifestDir "$PackageIdentifier.locale.en-US.yaml") $localeManifest
Set-Utf8File (Join-Path $manifestDir "$PackageIdentifier.installer.yaml") $installerManifest

Write-Host "Wrote winget manifests to $manifestDir"
