#Requires -Version 5.1
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$startBat = Join-Path $repo "start.bat"
if (-not (Test-Path $startBat)) { throw "Missing start.bat" }

$launcherProj = Join-Path $repo "tools\RebuildLauncher\RebuildLauncher.csproj"
$publishDir = Join-Path $repo "tools\RebuildLauncher\publish"
Write-Host "Publishing rebuild launcher (single-file)..."
& dotnet publish $launcherProj -c Release -r win-x64 --self-contained false -o $publishDir -nologo | Out-Host
$pubExe = Join-Path $publishDir "Better-Muv-Rebuild.exe"
if (-not (Test-Path $pubExe)) { throw "Publish failed" }

$stableExe = Join-Path $repo "Better-Muv-Rebuild.exe"
Copy-Item $pubExe $stableExe -Force
# 单文件仍可能带 runtimeconfig；一并放到仓库根，避免主机找不到配置。
foreach ($name in @(
    "Better-Muv-Rebuild.runtimeconfig.json",
    "Better-Muv-Rebuild.deps.json"
)) {
    $src = Join-Path $publishDir $name
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $repo $name) -Force
    }
}

$iconExe = Join-Path $repo "bin\Debug\net10.0-windows10.0.19041.0\Better-Muv.exe"
if (-not (Test-Path $iconExe)) {
  & dotnet build (Join-Path $repo "Better-Muv.csproj") -c Debug -nologo | Out-Host
}
if (-not (Test-Path $iconExe)) { $iconExe = $stableExe }

$desktop = [Environment]::GetFolderPath("Desktop")
$programs = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$taskBarDir = Join-Path $env:APPDATA "Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"
New-Item -ItemType Directory -Path $taskBarDir -Force | Out-Null
$lnkName = "Better-Muv Rebuild.lnk"

function Make-Lnk([string]$path) {
  $sh = New-Object -ComObject WScript.Shell
  $sc = $sh.CreateShortcut($path)
  $sc.TargetPath = $stableExe
  $sc.Arguments = ""
  $sc.WorkingDirectory = $repo
  $sc.IconLocation = "$iconExe,0"
  $sc.Description = "Rebuild Debug and launch Better-Muv"
  $sc.Save()
}

Make-Lnk (Join-Path $desktop $lnkName)
Make-Lnk (Join-Path $programs $lnkName)
Make-Lnk (Join-Path $taskBarDir $lnkName)

# 冒烟：启动器应能找到仓库并拉起 start.bat（不等待 build 结束）
$probe = Start-Process -FilePath $stableExe -WorkingDirectory $repo -PassThru
Start-Sleep -Seconds 2
if ($probe.HasExited -and $probe.ExitCode -ne 0) {
  throw "Launcher smoke failed, exit=$($probe.ExitCode)"
}
Write-Host "OK: $stableExe"
Write-Host "Desktop / Start Menu / TaskBar folder shortcuts recreated (Arguments cleared)."
Write-Host "Windows blocks auto-pin. Right-click Desktop shortcut -> Pin to taskbar."
Start-Process explorer.exe "/select,`"$(Join-Path $desktop $lnkName)`""
