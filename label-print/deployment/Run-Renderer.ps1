param([Parameter(Mandatory)][string]$ConfigPath)
$ErrorActionPreference='Stop'
$settings=Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$env:LABEL_RENDERER_API_KEY=$settings.ApiKey
$env:LABEL_RENDERER_PORT=[string]$settings.Port
$env:LABEL_CJK_FONT_PATH=$settings.RegularFont
$env:LABEL_CJK_BOLD_FONT_PATH=$settings.BoldFont
$process=Start-Process -FilePath $settings.PythonPath -ArgumentList @('-m','label_renderer') `
    -WorkingDirectory $settings.WorkingDirectory -WindowStyle Hidden -PassThru -Wait `
    -RedirectStandardError $settings.ErrorLog -RedirectStandardOutput $settings.OutputLog
exit $process.ExitCode
