param([switch]$NoBrowser)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$webDirectory = Join-Path $workspace 'web'
$packageJson = Join-Path $webDirectory 'package.json'
$viteEntry = Join-Path $webDirectory 'node_modules\vite\bin\vite.js'
$url = 'http://127.0.0.1:5175/'
$logDirectory = Join-Path $env:LOCALAPPDATA 'LabelSystem\logs'
$outputLog = Join-Path $logDirectory 'h5.out.log'
$errorLog = Join-Path $logDirectory 'h5.err.log'

if (-not (Test-Path -LiteralPath $packageJson -PathType Leaf)) {
    throw '找不到标签管理系统 H5 项目。'
}
$npm = Get-Command npm.cmd -ErrorAction Stop
$node = Get-Command node.exe -ErrorAction Stop
$nodeVersion = (& $node.Source -p 'process.versions.node').Trim()
if ($LASTEXITCODE -ne 0 -or [version]$nodeVersion -lt [version]'22.12.0') {
    throw '标签管理系统需要 Node.js 22.12.0 或更高版本。'
}

function Get-H5Listeners {
    @(Get-NetTCPConnection -State Listen -LocalPort 5175 -ErrorAction SilentlyContinue)
}

function Test-ProjectListener([object]$listener) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
    if (-not $process -or -not $process.CommandLine) { return $false }
    $normalizedCommand = $process.CommandLine.Replace('/', '\')
    return $normalizedCommand.IndexOf(
        $webDirectory,
        [StringComparison]::OrdinalIgnoreCase) -ge 0
}

$listeners = Get-H5Listeners
foreach ($listener in $listeners) {
    if (-not (Test-ProjectListener $listener)) {
        throw '本机 5175 端口已被其他程序占用。'
    }
}

if ($listeners.Count -eq 0) {
    if (-not (Test-Path -LiteralPath $viteEntry -PathType Leaf)) {
        Push-Location $webDirectory
        try {
            & $npm.Source ci
            if ($LASTEXITCODE -ne 0) { throw 'H5 依赖安装失败。' }
        } finally {
            Pop-Location
        }
    }

    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $escapedWeb = $webDirectory.Replace("'", "''")
    $escapedNpm = $npm.Source.Replace("'", "''")
    $command = "`$ErrorActionPreference='Stop'; Set-Location -LiteralPath '$escapedWeb'; & '$escapedNpm' run dev"
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encodedCommand `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outputLog `
        -RedirectStandardError $errorLog
}

$ready = $false
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    Start-Sleep -Milliseconds 250
    try {
        $runtime = Invoke-RestMethod -Uri ($url + 'runtime-config.json') -TimeoutSec 2
        if ([string]$runtime.h5Version) { $ready = $true; break }
    } catch { }
}
if (-not $ready) {
    throw "标签管理系统 H5 启动失败，请查看日志：$errorLog"
}

$apiStatus = '不可用'
$gatewayStatus = '不可用'
try {
    $health = Invoke-RestMethod -Uri 'http://172.16.1.159:9890/api/v1/health' -TimeoutSec 3
    if ($health.data.status) { $apiStatus = [string]$health.data.status }
} catch { }
try {
    $gateway = Invoke-RestMethod -Uri 'http://172.16.1.159:9880/api/v1/health' -TimeoutSec 3
    if ($gateway.data.status) { $gatewayStatus = [string]$gateway.data.status }
} catch { }

Write-Output "标签管理系统已启动：$url"
Write-Output "Business API：$apiStatus；U8 Gateway：$gatewayStatus"
if (-not $NoBrowser) { Start-Process $url }
