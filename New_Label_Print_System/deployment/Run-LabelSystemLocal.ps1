param([Parameter(Mandatory)][string]$ConfigPath)
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$release = Split-Path -Parent $settings.backendDirectory
$env:NEW_LABEL_SYSTEM_CONFIG = $ConfigPath
$env:PYTHONPATH = Join-Path $release 'site-packages'
$entry = Join-Path $settings.backendDirectory 'standalone_labelsystem.py'
$entryArgument = '"' + $entry + '"'
$process = Start-Process -FilePath $settings.pythonPath -ArgumentList @($entryArgument) `
    -WorkingDirectory $settings.backendDirectory -WindowStyle Hidden -PassThru -Wait `
    -RedirectStandardOutput $settings.outputLog -RedirectStandardError $settings.errorLog
exit $process.ExitCode
