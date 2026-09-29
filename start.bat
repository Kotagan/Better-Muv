@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "PROJ=%~dp0Better-Muv.csproj"
set "OUTDIR=%~dp0bin\Debug\net10.0-windows10.0.19041.0"
set "OBJDIR=%~dp0obj\Debug\net10.0-windows10.0.19041.0"
set "EXE=%OUTDIR%\Better-Muv.exe"
set "DLL=%OUTDIR%\Better-Muv.dll"

rem Read Version from csproj (do not hardcode).
for /f "usebackq delims=" %%I in (`powershell -NoProfile -Command ^
  "$raw=Get-Content -LiteralPath '%PROJ%' -Raw; if($raw -match '<Version>([^<]+)</Version>'){$Matches[1].Trim()}else{exit 1}"`) do set "APPVER=%%I"
if not defined APPVER (
  echo Failed to read ^<Version^> from Better-Muv.csproj
  pause
  exit /b 1
)

for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%I"
set "INFOVER=%APPVER%+local-%STAMP%"

echo Closing running Better-Muv (if any)...
taskkill /IM Better-Muv.exe /F >nul 2>&1
powershell -NoProfile -Command "Get-Process -Name 'Better-Muv' -ErrorAction SilentlyContinue | Stop-Process -Force" >nul 2>&1

set /a "_wait=0"
:wait_kill
tasklist /FI "IMAGENAME eq Better-Muv.exe" 2>nul | find /I "Better-Muv.exe" >nul
if errorlevel 1 goto kill_done
set /a "_wait+=1"
if %_wait% GEQ 20 (
  echo Better-Muv.exe is still running; close it and retry.
  pause
  exit /b 1
)
timeout /t 1 /nobreak >nul
goto wait_kill
:kill_done

echo Cleaning Debug outputs (bin + obj)...
dotnet clean "%PROJ%" -c Debug -nologo --verbosity quiet >nul 2>&1
powershell -NoProfile -Command ^
  "$ErrorActionPreference='Stop'; function Clear-Tree([string]$root){ if(-not (Test-Path -LiteralPath $root)){ return }; $empty=Join-Path $env:TEMP ('bm-empty-'+[guid]::NewGuid().ToString('n')); New-Item -ItemType Directory -Path $empty -Force|Out-Null; & robocopy $empty $root /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null; Remove-Item -LiteralPath $empty -Recurse -Force -ErrorAction SilentlyContinue; cmd /c ('rmdir /s /q \"\\?\'+$root+'\"') | Out-Null; if(Test-Path -LiteralPath $root){ throw \"still locked: $root\" } }; foreach($p in @('%OUTDIR%','%OBJDIR%')){ Clear-Tree $p }"
if errorlevel 1 (
  echo Clean failed: output folders still locked or path too long. Close Better-Muv / VS / antivirus and retry.
  pause
  exit /b 1
)

echo Building fresh Debug (%INFOVER%)...
dotnet build "%PROJ%" -c Debug -nologo --verbosity minimal ^
  -p:InformationalVersion=%INFOVER% ^
  -p:IncludeSourceRevisionInInformationalVersion=false ^
  --no-incremental
if errorlevel 1 (
  echo Build failed. If files were locked, close Better-Muv and retry.
  pause
  exit /b 1
)

if not exist "%EXE%" (
  echo EXE not found: %EXE%
  pause
  exit /b 1
)
if not exist "%DLL%" (
  echo DLL not found: %DLL%
  pause
  exit /b 1
)

powershell -NoProfile -Command ^
  "$dll=Get-Item -LiteralPath '%DLL%'; $exe=Get-Item -LiteralPath '%EXE%';" ^
  "$ver=[Diagnostics.FileVersionInfo]::GetVersionInfo($dll.FullName);" ^
  "$expect='%APPVER%+local-*';" ^
  "Write-Host ('EXE ready: {0}  {1} bytes' -f $exe.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), $exe.Length);" ^
  "Write-Host ('DLL ready: {0}  {1} bytes' -f $dll.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), $dll.Length);" ^
  "Write-Host ('Version : {0}' -f $ver.ProductVersion);" ^
  "if($ver.ProductVersion -notlike $expect){ Write-Host ('ERROR: expected {0}, got {1}' -f $expect, $ver.ProductVersion); exit 2 }"
if errorlevel 1 (
  echo Fresh-build verification failed.
  pause
  exit /b 1
)

echo Starting Debug build...
start "" "%EXE%"
endlocal
