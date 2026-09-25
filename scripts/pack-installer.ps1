#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Better-Muv (self-contained win-x64) and build an Inno Setup installer.
#>
param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root "Better-Muv.csproj"))) {
    $root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}
$project = Join-Path $root "Better-Muv.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $csprojRaw = Get-Content -LiteralPath $project -Raw
    if ($csprojRaw -notmatch '<Version>([^<]+)</Version>') {
        throw "Cannot read <Version> from Better-Muv.csproj"
    }
    $Version = $Matches[1].Trim()
    Write-Host "==> Version from csproj: $Version"
}

if ($Version -notmatch '^(\d+\.\d+\.\d+)(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid version: $Version"
}
$numericVersion = "$($Matches[1]).0"
if ($Runtime -notmatch '^win-(x64|x86|arm64)$') { throw "Invalid runtime: $Runtime" }
$publishRoot = [IO.Path]::GetFullPath((Join-Path $root "publish"))
$publishDir = [IO.Path]::GetFullPath((Join-Path $publishRoot "$Version\$Runtime"))
$distDir = Join-Path $root "dist"
$iss = Join-Path $root "installer\Better-Muv.iss"

Write-Host "==> Publish $project ($Configuration, $Runtime, v$Version)"
if (Test-Path $publishDir) {
    $resolvedPublishDir = (Resolve-Path -LiteralPath $publishDir).Path
    if (-not $resolvedPublishDir.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $publishDir).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unsafe publish directory: $resolvedPublishDir"
    }
    Remove-Item -LiteralPath $resolvedPublishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
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
    "/DAppNumericVersion=$numericVersion" `
    "/DPublishDir=$publishDir" `
    "/DOutputDir=$distDir" `
    $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$setup = Get-ChildItem $distDir -Filter "Better-Muv-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $setup) { throw "Setup exe not found in $distDir" }

Write-Host ""
Write-Host "OK: $($setup.FullName)"
Write-Host "Size: $([math]::Round($setup.Length / 1MB, 1)) MB"
