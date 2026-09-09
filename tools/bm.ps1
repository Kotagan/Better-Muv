# Better-Muv 调试入口（不进安装包）
# 用法: .\tools\bm.ps1 status
#       .\tools\bm.ps1 probe mainquest --json
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CommandArgs
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$Project = Join-Path $RepoRoot 'tools\BmCli\BmCli.csproj'

if (-not (Test-Path $Project)) {
    Write-Error "找不到 $Project"
}

$env:BETTER_MUV_ROOT = $RepoRoot.Path

$dotnetArgs = @('run', '--project', $Project, '-c', 'Debug', '--')
if ($env:BM_NO_BUILD -eq '1') {
    $exe = Join-Path $RepoRoot 'tools\BmCli\bin\Debug\net10.0-windows10.0.19041.0\BmCli.exe'
    if (Test-Path $exe) {
        & $exe @CommandArgs
        exit $LASTEXITCODE
    }
}

& dotnet @dotnetArgs @CommandArgs
exit $LASTEXITCODE
