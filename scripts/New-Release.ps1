<#
.SYNOPSIS
  Publishes SentientDesk (self-contained win-x64) and packs a Velopack release.

.DESCRIPTION
  Requires .NET 8 SDK and the Velopack CLI (vpk). Installs vpk globally if missing.
  Output: artifacts\releases\ (Setup.exe, nupkg, releases.*.json, etc.)

.EXAMPLE
  .\scripts\New-Release.ps1
  .\scripts\New-Release.ps1 -Version 0.1.1
  .\scripts\New-Release.ps1 -Upload
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$RepoUrl = "https://github.com/xNifty/sentientdesk-windows",
    [switch]$Upload,
    [string]$GitHubToken = $env:GITHUB_TOKEN
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$csproj = Join-Path $repoRoot "SentientDesk\SentientDesk.csproj"
if (-not (Test-Path $csproj)) {
    throw "Project not found: $csproj"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$proj = Get-Content $csproj
    $Version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = "0.1.0"
    }
}

$publishDir = Join-Path $repoRoot "artifacts\publish"
$releasesDir = Join-Path $repoRoot "artifacts\releases"

Write-Host "==> Publishing SentientDesk $Version (win-x64 self-contained)" -ForegroundColor Cyan

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null

dotnet publish $csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $publishDir "SentientDesk.exe"
if (-not (Test-Path $exe)) {
    throw "Publish succeeded but SentientDesk.exe was not found in $publishDir"
}

function Ensure-Vpk {
    $vpk = Get-Command "vpk" -ErrorAction SilentlyContinue
    if ($vpk) {
        return
    }

    Write-Host "==> Installing Velopack CLI (vpk)" -ForegroundColor Cyan
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to install vpk. Install manually: dotnet tool install -g vpk"
    }

    $tools = Join-Path $env:USERPROFILE ".dotnet\tools"
    if ($env:PATH -notlike "*$tools*") {
        $env:PATH = "$tools;$env:PATH"
    }

    if (-not (Get-Command "vpk" -ErrorAction SilentlyContinue)) {
        throw "vpk was installed but is not on PATH. Restart the shell or add $tools to PATH."
    }
}

Ensure-Vpk

Write-Host "==> Downloading previous GitHub release assets (for deltas)" -ForegroundColor Cyan
$downloadArgs = @("download", "github", "--repoUrl", $RepoUrl, "--outputDir", $releasesDir)
if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
    $downloadArgs += @("--token", $GitHubToken)
}
& vpk @downloadArgs
# First release / empty repo is fine — continue packing.
if ($LASTEXITCODE -ne 0) {
    Write-Host "No prior release assets (or download failed); packing a full release only." -ForegroundColor DarkYellow
}

Write-Host "==> Packing Velopack release $Version" -ForegroundColor Cyan
& vpk pack `
    --packId SentientDesk `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe SentientDesk.exe `
    --packTitle SentientDesk `
    --outputDir $releasesDir

if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed with exit code $LASTEXITCODE"
}

$setup = Get-ChildItem $releasesDir -Filter "*Setup*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

Write-Host ""
Write-Host "Release packages ready:" -ForegroundColor Green
Write-Host "  $releasesDir"
if ($setup) {
    Write-Host "  Installer: $($setup.FullName)"
}
Write-Host ""

if ($Upload) {
    Write-Host "==> Uploading to GitHub Releases" -ForegroundColor Cyan
    $tag = "v$Version"
    $uploadArgs = @(
        "upload", "github",
        "--repoUrl", $RepoUrl,
        "--outputDir", $releasesDir,
        "--publish",
        "--releaseName", "SentientDesk $Version",
        "--tag", $tag
    )
    if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
        $uploadArgs += @("--token", $GitHubToken)
    }
    & vpk @uploadArgs
    if ($LASTEXITCODE -ne 0) {
        throw "vpk upload failed with exit code $LASTEXITCODE"
    }
    Write-Host "Published GitHub release $tag" -ForegroundColor Green
}
else {
    Write-Host "Tip: push a v$Version tag to publish via GitHub Actions, or re-run with -Upload." -ForegroundColor DarkYellow
}

Write-Host "Note: unsigned builds trigger SmartScreen warnings — expected for hobby releases." -ForegroundColor DarkYellow
