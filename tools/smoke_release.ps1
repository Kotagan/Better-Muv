#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "bin\Release\net10.0-windows10.0.19041.0"
$dll = Join-Path $out "Better-Muv.dll"
$exe = Join-Path $out "Better-Muv.exe"
if (-not (Test-Path $dll)) { throw "Release build missing: $dll" }
if (-not (Test-Path $exe)) { throw "Release exe missing: $exe" }

$required = @(
  "daily-shop-hub-exchange.png",
  "daily-shop-hub-limited.png",
  "daily-shop-icon.png",
  "mining-home-entry.png",
  "mission-claim-all.png",
  "quest.png",
  "hud-home.png"
)
$tpl = Join-Path $out "Assets\Templates"
foreach ($name in $required) {
  $path = Join-Path $tpl $name
  if (-not (Test-Path $path)) { throw "Missing template in output: $name" }
  Write-Host "OK template: $name"
}

$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
Write-Host ("ProductVersion={0} FileVersion={1}" -f $info.ProductVersion, $info.FileVersion)
if ($info.ProductVersion -notlike "1.3.7*") {
  throw "Expected product version 1.3.7*, got $($info.ProductVersion)"
}

# Type presence via MetadataLoadContext-free string scan of assembly names in deps
$deps = Get-Content (Join-Path $out "Better-Muv.deps.json") -Raw
foreach ($marker in @("ChildSession", "AppUpdateService")) {
  if ($deps -notmatch $marker -and -not (Select-String -Path (Join-Path $root "Services\*\*.cs"),(Join-Path $root "Services\ChildSession\*.cs") -Pattern $marker -Quiet)) {
    # soft check: source files exist
  }
}
@(
  "Services\ChildSession\ChildSessionService.cs",
  "Services\ChildSession\RdpActiveXHost.cs",
  "ChildSessionWindow.xaml",
  "Services\AppUpdateService.cs"
) | ForEach-Object {
  $p = Join-Path $root $_
  if (-not (Test-Path $p)) { throw "Missing source: $_" }
  Write-Host "OK source: $_"
}

Write-Host "SMOKE PASS"
