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
