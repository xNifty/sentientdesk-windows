<#
.SYNOPSIS
  Publishes SentientDesk (self-contained win-x64), builds a portable zip, and packs a Velopack release.

.DESCRIPTION
  Requires .NET 8 SDK and the Velopack CLI (vpk). Installs vpk globally if missing.
  Outputs:
    artifacts\portable\SentientDesk-Portable-<version>.zip
    artifacts\releases\ (Setup.exe, .msi, nupkg, releases.*.json, etc.)
  -Upload also attaches the portable zip via gh after vpk upload (needs gh + token).

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
$portableDir = Join-Path $repoRoot "artifacts\portable"

Write-Host "==> Publishing SentientDesk $Version (win-x64 self-contained)" -ForegroundColor Cyan

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null
New-Item -ItemType Directory -Path $portableDir -Force | Out-Null

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

Write-Host "==> Creating portable zip" -ForegroundColor Cyan

$portableZip = Join-Path $portableDir "SentientDesk-Portable-$Version.zip"
$portableBundle = Join-Path $portableDir "SentientDesk"
if (Test-Path $portableZip) {
    Remove-Item $portableZip -Force
}
if (Test-Path $portableBundle) {
    Remove-Item $portableBundle -Recurse -Force
}

New-Item -ItemType Directory -Path $portableBundle -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $portableBundle -Recurse -Force
Compress-Archive -Path $portableBundle -DestinationPath $portableZip -CompressionLevel Optimal
Remove-Item $portableBundle -Recurse -Force

if (-not (Test-Path $portableZip)) {
    throw "Portable zip was not created: $portableZip"
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

$conclusion = Join-Path $repoRoot "installer\conclusion.md"
if (-not (Test-Path $conclusion)) {
    throw "MSI conclusion text not found: $conclusion"
}

Write-Host "==> Packing Velopack release $Version (Setup.exe + MSI)" -ForegroundColor Cyan
& vpk pack `
    --packId SentientDesk `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe SentientDesk.exe `
    --packTitle SentientDesk `
    --packAuthors SentientDesk `
    --msi `
    --instLocation Either `
    --instConclusion $conclusion `
    --outputDir $releasesDir

if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed with exit code $LASTEXITCODE"
}

$setup = Get-ChildItem $releasesDir -Filter "*Setup*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$msi = Get-ChildItem $releasesDir -Filter "*.msi" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

Write-Host ""
Write-Host "Release packages ready:" -ForegroundColor Green
Write-Host "  Portable:  $portableZip"
Write-Host "  Velopack:  $releasesDir"
if ($msi) {
    Write-Host "  MSI (folder choice + launch checkbox): $($msi.FullName)"
}
if ($setup) {
    Write-Host "  Setup.exe (one-click): $($setup.FullName)"
}
Write-Host ""

if ($Upload) {
    Write-Host "==> Uploading Velopack assets to GitHub Releases" -ForegroundColor Cyan
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

    Write-Host "==> Attaching portable zip to $tag" -ForegroundColor Cyan
    if (-not (Get-Command "gh" -ErrorAction SilentlyContinue)) {
        throw "gh CLI not found. Install GitHub CLI to attach the portable zip, or upload $portableZip manually."
    }
    if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
        $env:GH_TOKEN = $GitHubToken
    }
    & gh release upload $tag $portableZip --clobber
    if ($LASTEXITCODE -ne 0) {
        throw "gh release upload failed with exit code $LASTEXITCODE"
    }
    Write-Host "Attached portable zip to $tag" -ForegroundColor Green
}
else {
    Write-Host "Tip: push a v$Version tag to publish via GitHub Actions, or re-run with -Upload." -ForegroundColor DarkYellow
}

Write-Host "Note: unsigned builds trigger SmartScreen warnings — expected for hobby releases." -ForegroundColor DarkYellow
