param([Parameter(Mandatory)][string]$ConfigPath)
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$env:NEW_LABEL_SYSTEM_CONFIG = $ConfigPath
$process = Start-Process -FilePath $settings.pythonPath -ArgumentList @('standalone_labelsystem.py') `
    -WorkingDirectory $settings.backendDirectory -WindowStyle Hidden -PassThru -Wait `
    -RedirectStandardOutput $settings.outputLog -RedirectStandardError $settings.errorLog
exit $process.ExitCode
