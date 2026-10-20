[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$PrivateSettingsPath,
    [string]$SeedDataPath,
    [string]$SourceHost = '172.16.1.159',
    [int]$ApiPort = 9891,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'NewLabelPrintSystem'),
    [string]$PythonPath
)
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($InstallRoot)
$localRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA + [IO.Path]::DirectorySeparatorChar)
if (-not $root.StartsWith($localRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Local installation must stay under LOCALAPPDATA.'
}
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$privatePath = (Resolve-Path -LiteralPath $PrivateSettingsPath).Path
$private = Get-Content -LiteralPath $privatePath -Raw | ConvertFrom-Json
foreach ($name in 'u8Server','u8User','u8Password','printSourceSigningKey') {
    if (-not [string]$private.$name) { throw "Private setting is missing: $name" }
}
if (([string]$private.u8Server).Split(',')[0].Trim() -ne $SourceHost) {
    throw 'U8 source must use the configured source host.'
}
if ([string]$private.u8Database -and ([string]$private.u8Database).ToLowerInvariant() -ne 'ufdata_888_2021') {
    throw 'Only development account 888 is allowed.'
}
if (-not $PythonPath) {
    $python = Get-Command python.exe -ErrorAction Stop
    $PythonPath = $python.Source
}
$version = & $PythonPath -c 'import sys; print(f"{sys.version_info.major}.{sys.version_info.minor}")'
if ($LASTEXITCODE -ne 0 -or $version.Trim() -ne '3.12') {
    throw 'Python 3.12 is required.'
}

New-Item -ItemType Directory -Path $root -Force | Out-Null
$stamp = Get-Date -Format yyyyMMdd_HHmmss
$release = Join-Path $root ('releases\' + $stamp)
New-Item -ItemType Directory -Path $release -Force | Out-Null
Expand-Archive -LiteralPath $package -DestinationPath $release
foreach ($required in 'backend\standalone_labelsystem.py','frontend\dist\index.html','site-packages') {
    if (-not (Test-Path -LiteralPath (Join-Path $release $required))) {
        throw "Package is incomplete: $required"
    }
}

$data = Join-Path $root 'Data'
if ($SeedDataPath -and -not (Test-Path -LiteralPath (Join-Path $data 'pilot.db'))) {
    $seed = (Resolve-Path -LiteralPath $SeedDataPath).Path
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    Get-ChildItem -LiteralPath $seed -Force | Copy-Item -Destination $data -Recurse -Force
}
New-Item -ItemType Directory -Path $data -Force | Out-Null
$configFolder = Join-Path $root 'Config'
$logs = Join-Path $root 'logs'
New-Item -ItemType Directory -Path $configFolder,$logs -Force | Out-Null
$apiOrigin = "http://${SourceHost}:$ApiPort"
$settings = [ordered]@{
    sourceHost = $SourceHost
    labelSystemApiUrl = $apiOrigin
    labelSystemApiHttpOrigin = $apiOrigin
    u8Database = 'UFDATA_888_2021'
    u8Server = [string]$private.u8Server
    u8User = [string]$private.u8User
    u8Password = [string]$private.u8Password
    u8OdbcDriver = if ([string]$private.u8OdbcDriver) { [string]$private.u8OdbcDriver } else { 'ODBC Driver 17 for SQL Server' }
    hosts = @('127.0.0.1')
    port = 18003
    dataDirectory = $data
    pythonPath = [IO.Path]::GetFullPath($PythonPath)
    backendDirectory = Join-Path $release 'backend'
    regularFont = if (Test-Path -LiteralPath "$env:WINDIR\Fonts\msyh.ttc") { "$env:WINDIR\Fonts\msyh.ttc" } else { '' }
    boldFont = if (Test-Path -LiteralPath "$env:WINDIR\Fonts\msyhbd.ttc") { "$env:WINDIR\Fonts\msyhbd.ttc" } else { '' }
    outputLog = Join-Path $logs 'standalone.out.log'
    errorLog = Join-Path $logs 'standalone.err.log'
    printSourceSigningKey = [string]$private.printSourceSigningKey
}
$account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
foreach ($folder in $configFolder,$data,$logs) {
    & icacls.exe $folder /inheritance:r /grant:r "${account}:(OI)(CI)F" `
        '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not secure $folder" }
    Get-ChildItem -LiteralPath $folder -Recurse -Force | ForEach-Object {
        & icacls.exe $_.FullName /inheritance:e /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not inherit permissions: $($_.FullName)" }
    }
}
$configPath = Join-Path $configFolder 'standalone.json'
[IO.File]::WriteAllText(
    $configPath,
    ($settings | ConvertTo-Json -Depth 6),
    (New-Object Text.UTF8Encoding($false)))
& icacls.exe $configPath /inheritance:e /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not secure the local configuration.' }

Get-CimInstance Win32_Process | Where-Object {
    $_.CommandLine -and $_.CommandLine.Contains('standalone_labelsystem.py') -and
    $_.ExecutablePath -and
    [IO.Path]::GetFullPath($_.ExecutablePath) -eq
    [IO.Path]::GetFullPath([string]$settings.pythonPath)
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Milliseconds 300
$listeners = Get-NetTCPConnection -LocalPort 18003 -State Listen -ErrorAction SilentlyContinue
foreach ($listener in $listeners) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
    if (-not $process -or
        [IO.Path]::GetFullPath($process.ExecutablePath) -ne
        [IO.Path]::GetFullPath([string]$settings.pythonPath)) {
        throw 'Local port 18003 is already used by another program.'
    }
    Stop-Process -Id $listener.OwningProcess -Force
}

$runner = Join-Path $release 'deployment\Run-LabelSystemLocal.ps1'
$manualStart = Join-Path $root 'Start-LabelPrintSystem.ps1'
Copy-Item -LiteralPath (Join-Path $release 'deployment\Start-LabelPrintSystem.ps1') `
    -Destination $manualStart -Force
$startupFolder = [Environment]::GetFolderPath('Startup')
foreach ($name in 'NewLabelPrintSystem.vbs','LabelPrintingAssistant.vbs') {
    $automatic = Join-Path $startupFolder $name
    if (Test-Path -LiteralPath $automatic) {
        Move-Item -LiteralPath $automatic -Destination ($automatic + '.disabled') -Force
    }
}
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) '启动标签打印系统.cmd'
$oldDesktop = Join-Path ([Environment]::GetFolderPath('Desktop')) '标签打印系统.url'
if (Test-Path -LiteralPath $oldDesktop) { Remove-Item -LiteralPath $oldDesktop -Force }
if (Test-Path -LiteralPath $desktop) { Remove-Item -LiteralPath $desktop -Force }
$runArguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' +
    $runner + '" -ConfigPath "' + $configPath + '"'
Start-Process -FilePath 'powershell.exe' -ArgumentList $runArguments -WindowStyle Hidden

$healthy = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    try {
        $result = Invoke-RestMethod 'http://127.0.0.1:18003/api/health' -TimeoutSec 2
        if ($result.checks.database.ok) { $healthy = $true; break }
    } catch { }
}
if (-not $healthy) { throw "Local printing workspace failed to start. See $($settings.errorLog)" }
$matches = @(
    Get-NetTCPConnection -LocalPort 18003 -State Listen -ErrorAction Stop |
        Where-Object LocalAddress -eq '127.0.0.1' | ForEach-Object {
            $running = Get-CimInstance Win32_Process -Filter "ProcessId=$($_.OwningProcess)"
            if ($running -and $running.CommandLine -and
                $running.CommandLine.Contains([string]$settings.backendDirectory)) {
                $running
            }
        }
)
if ($matches.Count -ne 1) {
    throw 'Health check reached a different local printing release.'
}
Stop-Process -Id $matches[0].ProcessId -Force
for ($i = 0; $i -lt 20; $i++) {
    if (-not (Get-NetTCPConnection -LocalPort 18003 -State Listen -ErrorAction SilentlyContinue)) {
        break
    }
    Start-Sleep -Milliseconds 100
}
[pscustomobject]@{
    url = 'http://127.0.0.1:18003/bills'
    source = $apiOrigin
    account = '888'
    release = $release
    data = $data
    manualStart = $manualStart
}
