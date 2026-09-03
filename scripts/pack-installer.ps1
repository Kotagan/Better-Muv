#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Better-Muv (self-contained win-x64) and build an Inno Setup installer.
#>
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root "Better-Muv.csproj"))) {
    $root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$publishDir = Join-Path $root "publish\$Runtime"
$distDir = Join-Path $root "dist"
$iss = Join-Path $root "installer\Better-Muv.iss"
$project = Join-Path $root "Better-Muv.csproj"

Write-Host "==> Publish $project ($Configuration, $Runtime, v$Version)"
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version.0 `
    -p:FileVersion=$Version.0 `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Ensure default config ships with the installer payload.
Copy-Item (Join-Path $root "config.json") (Join-Path $publishDir "config.json") -Force

$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6 first (e.g. winget install JRSoftware.InnoSetup)."
}

Write-Host "==> Compile installer with $iscc"
& $iscc `
    "/DAppVersion=$Version" `
    "/DPublishDir=$publishDir" `
    "/DOutputDir=$distDir" `
    $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$setup = Get-ChildItem $distDir -Filter "Better-Muv-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw "Setup exe not found in $distDir" }

Write-Host ""
Write-Host "OK: $($setup.FullName)"
Write-Host "Size: $([math]::Round($setup.Length / 1MB, 1)) MB"
