@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0bin\Debug\net10.0-windows10.0.19041.0\Better-Muv.exe"
if not exist "%EXE%" (
  echo Building Better-Muv...
  dotnet build "%~dp0Better-Muv.csproj" -c Debug -nologo
  if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
  )
)

start "" "%EXE%"
endlocal
