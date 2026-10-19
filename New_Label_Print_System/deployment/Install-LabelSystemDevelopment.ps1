param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ApiConfigPath,
    [Parameter(Mandatory)][string]$ExistingDevelopmentRuntime,
    [Parameter(Mandatory)][string]$ListenAddress
)
$ErrorActionPreference = 'Stop'
# Run on the development server. No migration, production site, or print helper.
$api = Get-Content -LiteralPath $ApiConfigPath -Raw | ConvertFrom-Json
if ($api.LabelSystem.Environment -ne 'development' -or $api.LabelSystem.ErpAccountId -ne '888' -or
    $api.LabelSystem.ErpDatabaseName -ne 'UFDATA_888_2021' -or $api.Printing.AllowNewDispatch) {
    throw 'Requires the existing development API with account 888 and dispatch disabled.'
}
$sql = New-Object System.Data.SqlClient.SqlConnectionStringBuilder([string]$api.ConnectionStrings.LabelSystem)
if ($sql['Initial Catalog'] -ne 'LabelSystem_Dev') { throw 'Unexpected development database.' }
$root = 'D:\NewLabelPrintDevelopment'
if ((Get-ScheduledTask -TaskName 'NewLabelPrintDevelopment' -ErrorAction SilentlyContinue).State -eq 'Running') {
    throw 'Independent development task is already running. Use a planned update; do not replace its active configuration.'
}
$release = Join-Path $root ('releases\' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $release -Force | Out-Null
Expand-Archive -LiteralPath $PackagePath -DestinationPath $release
if (-not (Test-Path -LiteralPath (Join-Path $release 'site-packages') -PathType Container)) {
    throw 'A new installation needs a complete package including locked dependencies.'
}
if (-not (Test-Path -LiteralPath (Join-Path $release 'connector\dist\blg-connector-windows.exe') -PathType Leaf)) {
    throw 'Missing Windows assistant. Build connector/build-windows.ps1 and include connector/dist/blg-connector-windows.exe in the package.'
}
$python = Join-Path $release 'python'
New-Item -ItemType Directory -Path $python -Force | Out-Null
# Reuse a previously provisioned Python 3.12 core; dependencies come from uv.lock.
Get-ChildItem -LiteralPath (Join-Path $ExistingDevelopmentRuntime 'python') -Force |
    Where-Object Name -ne 'Lib' | Copy-Item -Destination $python -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $python 'Lib') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $ExistingDevelopmentRuntime 'python\Lib') -Force |
    Where-Object Name -ne 'site-packages' |
    Copy-Item -Destination (Join-Path $python 'Lib') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $release 'site-packages') -Destination (Join-Path $python 'Lib') -Recurse
Copy-Item -LiteralPath (Join-Path $ExistingDevelopmentRuntime 'fonts') -Destination $release -Recurse
foreach ($folder in 'Config','Data','logs') {
    New-Item -ItemType Directory -Path (Join-Path $root $folder) -Force | Out-Null
}
$config = Join-Path $root 'Config\standalone.json'
$settings = @{
    labelSystemApiUrl = 'http://127.0.0.1:9891'; u8Database = 'UFDATA_888_2021'
    u8Server = $sql['Data Source']; u8User = $sql['User ID']; u8Password = $sql['Password']
    u8OdbcDriver = 'ODBC Driver 17 for SQL Server'; hosts = @('127.0.0.1',$ListenAddress); port = 18003
    dataDirectory = (Join-Path $root 'Data'); pythonPath = (Join-Path $python 'python.exe')
    backendDirectory = (Join-Path $release 'backend')
    regularFont = (Join-Path $release 'fonts\msyh.ttc'); boldFont = (Join-Path $release 'fonts\msyhbd.ttc')
    outputLog = (Join-Path $root 'logs\standalone.out.log'); errorLog = (Join-Path $root 'logs\standalone.err.log')
}
# Restrict the containing directory before writing credentials.
& icacls (Join-Path $root 'Config') /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Configuration ACL failed.' }
$settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $config -Encoding UTF8
& icacls $release /grant '*S-1-5-19:(OI)(CI)RX' /T /Q | Out-Null
& icacls (Join-Path $root 'Data') /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)M' | Out-Null
& icacls (Join-Path $root 'logs') /grant '*S-1-5-19:(OI)(CI)M' | Out-Null
$run = Join-Path $release 'deployment\Run-LabelSystemDevelopment.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -ConfigPath "{1}"' -f $run,$config)
$principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\LOCAL SERVICE' -LogonType ServiceAccount
$trigger = New-ScheduledTaskTrigger -AtStartup
$taskSettings = New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName 'NewLabelPrintDevelopment' -Action $action -Principal $principal -Trigger $trigger -Settings $taskSettings -Force | Out-Null
if (-not (Get-NetFirewallRule -Name 'NewLabelPrintDevelopment-18003' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name 'NewLabelPrintDevelopment-18003' -DisplayName 'Independent label print development' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 18003 -RemoteAddress LocalSubnet -Profile Domain,Private | Out-Null
}
Start-ScheduledTask -TaskName 'NewLabelPrintDevelopment'
[pscustomobject]@{ release=$release; url="http://${ListenAddress}:18003"; account='888'; authentication='Label system'; printDispatch=$false }
