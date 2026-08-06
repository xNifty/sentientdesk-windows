<#
.SYNOPSIS
  Publishes SentientDesk (self-contained win-x64) and builds an Inno Setup installer.

.DESCRIPTION
  Requires .NET 8 SDK and Inno Setup 6 (ISCC.exe).
  Output: artifacts\installer\SentientDesk-Setup-<version>.exe

.EXAMPLE
  .\scripts\New-Installer.ps1
  .\scripts\New-Installer.ps1 -Version 0.1.1
  .\scripts\New-Installer.ps1 -IsccPath 'O:\Program Files (x86)\Inno Setup 6\ISCC.exe'
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$IsccPath = ""
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
$installerDir = Join-Path $repoRoot "artifacts\installer"
$iss = Join-Path $repoRoot "installer\SentientDesk.iss"

Write-Host "==> Publishing SentientDesk $Version (win-x64 self-contained)" -ForegroundColor Cyan

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

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

function Find-ISCC {
    param([string]$ExplicitPath)

    if ($ExplicitPath) {
        if (-not (Test-Path $ExplicitPath)) {
            throw "ISCC.exe not found at -IsccPath: $ExplicitPath"
        }
        return (Resolve-Path $ExplicitPath).Path
    }

    $candidates = @(
        (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
        # Local install on O: (non-default drive)
        "O:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "O:\Program Files\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    return $candidates | Select-Object -First 1
}

$iscc = Find-ISCC -ExplicitPath $IsccPath
if (-not $iscc) {
    throw @"
Inno Setup 6 was not found (ISCC.exe).

Install it from https://jrsoftware.org/isdl.php
then re-run this script, or pass -IsccPath 'O:\...\ISCC.exe'.
"@
}

Write-Host "==> Compiling installer with $iscc" -ForegroundColor Cyan

& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}

$setup = Join-Path $installerDir "SentientDesk-Setup-$Version.exe"
if (-not (Test-Path $setup)) {
    throw "Installer output not found: $setup"
}

Write-Host ""
Write-Host "Installer ready:" -ForegroundColor Green
Write-Host "  $setup"
Write-Host ""
Write-Host "Note: unsigned builds trigger SmartScreen warnings — expected for hobby releases." -ForegroundColor DarkYellow
