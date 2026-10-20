param(
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$GoCommand = 'go'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath($OutputPath)
& (Join-Path $repo 'connector\build-windows.ps1') -GoCommand $GoCommand
Push-Location (Join-Path $repo 'frontend')
try {
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
} finally { Pop-Location }
$python = Join-Path $repo 'backend\.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Run uv sync in backend before packaging.' }
& $python (Join-Path $PSScriptRoot 'build_package.py') --output $destination
if ($LASTEXITCODE -ne 0) { throw 'Local package failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-LabelSystemLocal.ps1') `
    -Destination (Join-Path (Split-Path -Parent $destination) 'Install-LabelSystemLocal.ps1') -Force
Copy-Item -LiteralPath (Join-Path $repo '启动标签打印系统.cmd') `
    -Destination (Join-Path (Split-Path -Parent $destination) '启动标签打印系统.cmd') -Force
