param(
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$GoCommand='go',
    [switch]$ApplicationOnly
)
$ErrorActionPreference='Stop'
$repo = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath($OutputPath)
& (Join-Path $repo 'connector\build-windows.ps1') -GoCommand $GoCommand
Push-Location (Join-Path $repo 'frontend')
try { & npm run build; if($LASTEXITCODE -ne 0){throw 'Frontend build failed.'} }
finally { Pop-Location }
$argsForPackage = @((Join-Path $PSScriptRoot 'build_package.py'),'--output',$destination)
if($ApplicationOnly){$argsForPackage += '--application-only'}
& (Join-Path $repo 'backend\.venv\Scripts\python.exe') @argsForPackage
if($LASTEXITCODE -ne 0){throw 'Standalone package failed.'}
