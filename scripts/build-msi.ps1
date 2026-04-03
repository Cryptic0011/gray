[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repoRoot "artifacts"
$appPublishDir = Join-Path $artifactsRoot "app"
$cliPublishDir = Join-Path $artifactsRoot "cli"
$distDir = Join-Path $repoRoot "dist"
$installerProject = Join-Path $repoRoot "src\Gmux.Setup\Gmux.Setup.wixproj"

if (Test-Path -LiteralPath $appPublishDir) {
    Remove-Item -Recurse -Force -LiteralPath $appPublishDir
}

if (Test-Path -LiteralPath $cliPublishDir) {
    Remove-Item -Recurse -Force -LiteralPath $cliPublishDir
}

New-Item -ItemType Directory -Force -Path $appPublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $cliPublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

dotnet publish (Join-Path $repoRoot "src\Gmux.App\Gmux.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o $appPublishDir

dotnet publish (Join-Path $repoRoot "src\Gmux.Cli\Gmux.Cli.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o $cliPublishDir

dotnet build $installerProject `
    -c $Configuration `
    -p:AppPublishDir=$appPublishDir `
    -p:CliPublishDir=$cliPublishDir

$msiPath = Get-ChildItem -Path (Join-Path $repoRoot "src\Gmux.Setup\bin\$Configuration") -Filter *.msi -Recurse |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $msiPath) {
    throw "MSI output was not produced."
}

Copy-Item -LiteralPath $msiPath -Destination (Join-Path $distDir "gray-installer-win-x64.msi") -Force
