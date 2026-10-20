$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'NewLabelPrintSystem'
$configPath = Join-Path $root 'Config\standalone.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    throw '标签打印系统尚未安装。'
}

$settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$release = Split-Path -Parent ([string]$settings.backendDirectory)
$runner = Join-Path $release 'deployment\Run-LabelSystemLocal.ps1'
if (-not (Test-Path -LiteralPath $runner)) {
    throw '找不到标签打印系统启动程序。'
}

$web = @(Get-NetTCPConnection -State Listen -LocalPort 18003 -ErrorAction SilentlyContinue |
    Where-Object LocalAddress -eq '127.0.0.1')
foreach ($listener in $web) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
    if (-not $process.CommandLine -or
        -not $process.CommandLine.Contains([string]$settings.backendDirectory)) {
        throw '本机18003端口已被其他程序占用。'
    }
}
if ($web.Count -eq 0) {
    $arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' +
        $runner + '" -ConfigPath "' + $configPath + '"'
    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden
}

$assistantRoot = Join-Path $env:LOCALAPPDATA 'LabelPrintingAssistant'
$assistantExe = Join-Path $assistantRoot 'blg-connector.exe'
$assistantConfig = Join-Path $assistantRoot 'config.yaml'
$assistant = @(Get-NetTCPConnection -State Listen -LocalPort 9110 -ErrorAction SilentlyContinue |
    Where-Object LocalAddress -eq '127.0.0.1')
foreach ($listener in $assistant) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
    if (-not $process.ExecutablePath -or
        [IO.Path]::GetFullPath($process.ExecutablePath) -ne [IO.Path]::GetFullPath($assistantExe)) {
        throw '本机9110端口已被其他程序占用。'
    }
}
if ($assistant.Count -eq 0 -and (Test-Path -LiteralPath $assistantExe) -and
    (Test-Path -LiteralPath $assistantConfig)) {
    Start-Process -FilePath $assistantExe `
        -ArgumentList ('-config "' + $assistantConfig + '"') -WindowStyle Hidden
}

$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    try {
        $health = Invoke-RestMethod -Uri 'http://127.0.0.1:18003/api/health' -TimeoutSec 2
        if ($health.status -eq 'ok') { $ready = $true; break }
    } catch { }
}
if (-not $ready) {
    throw '标签打印系统启动失败，请查看本地日志。'
}

Start-Process 'http://127.0.0.1:18003/bills'
