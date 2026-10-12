[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Server,
    [string]$ConfigPath,
    [pscredential]$Credential,
    [string]$PackagePath,
    [int]$Port = 9890
)

$ErrorActionPreference = 'Stop'
$apiRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $apiRoot 'runtime-data\publish\LabelSystem.Api'
}
$packageRoot = [IO.Path]::GetFullPath($PackagePath)
$configFile = if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $null } else { [IO.Path]::GetFullPath($ConfigPath) }

if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'LabelSystem.Api.dll'))) {
    throw "IIS 发布包无效：$packageRoot"
}
if ($null -ne $configFile) {
    if (-not (Test-Path -LiteralPath $configFile)) {
        throw "外置配置不存在：$configFile"
    }
    $config = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($config.ConnectionStrings.LabelSystem) -or
        [string]::IsNullOrWhiteSpace($config.U8Gateway.ApiKey)) {
        throw '外置配置缺少数据库连接串或 U8 Gateway API Key。'
    }
}

$publishRoot = Join-Path $apiRoot 'runtime-data\publish'
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
$zipPath = Join-Path $publishRoot 'LabelSystem.Api.zip'
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

$sessionParameters = @{
    ComputerName = $Server
    Authentication = 'Negotiate'
}
if ($Credential) { $sessionParameters.Credential = $Credential }
$session = New-PSSession @sessionParameters
try {
    $preflight = Invoke-Command -Session $session -ScriptBlock {
        param($RequestedPort)
        Import-Module WebAdministration
        [pscustomobject]@{
            HostingModule = Test-Path 'C:\Program Files\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
            ConflictingBindings = @(
                Get-WebBinding | Where-Object {
                    $_.bindingInformation -match (':' + $RequestedPort + ':') -and
                    $_.ItemXPath -notmatch "site\[@name='LabelSystemApi'"
                }
            ).Count
        }
    } -ArgumentList $Port
    if (-not $preflight.HostingModule) {
        throw '目标服务器尚未安装 ASP.NET Core Hosting Bundle。'
    }
    if ($preflight.ConflictingBindings -gt 0) {
        throw "目标服务器端口 $Port 已被其他 IIS 站点占用。"
    }

    Invoke-Command -Session $session -ScriptBlock {
        New-Item -ItemType Directory -Path 'D:\LabelSystem\Api\incoming' -Force | Out-Null
        New-Item -ItemType Directory -Path 'D:\LabelSystem\Config' -Force | Out-Null
    }
    Copy-Item -LiteralPath $zipPath -Destination 'D:\LabelSystem\Api\incoming\LabelSystem.Api.zip' -ToSession $session -Force
    if ($null -ne $configFile) {
        Copy-Item -LiteralPath $configFile -Destination 'D:\LabelSystem\Config\label-api.json.new' -ToSession $session -Force
    }
    else {
        $remoteConfigExists = Invoke-Command -Session $session -ScriptBlock {
            Test-Path -LiteralPath 'D:\LabelSystem\Config\label-api.json'
        }
        if (-not $remoteConfigExists) {
            throw '未提供本机配置，服务器上也没有现有外置配置。'
        }
    }

    Invoke-Command -Session $session -ScriptBlock {
        param($RequestedPort)
        $ErrorActionPreference = 'Stop'
        Import-Module WebAdministration

        $siteName = 'LabelSystemApi'
        $poolName = 'LabelSystemApiPool'
        $releaseId = (Get-Date).ToString('yyyyMMdd_HHmmss')
        $releasePath = "D:\LabelSystem\Api\releases\$releaseId"
        $zipPath = 'D:\LabelSystem\Api\incoming\LabelSystem.Api.zip'
        $configPath = 'D:\LabelSystem\Config\label-api.json'
        $newConfigPath = 'D:\LabelSystem\Config\label-api.json.new'
        $archivePath = 'D:\LabelSystem\OperationArchive'

        New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
        New-Item -ItemType Directory -Path $archivePath -Force | Out-Null
        Expand-Archive -LiteralPath $zipPath -DestinationPath $releasePath -Force

        [xml]$webConfig = Get-Content -LiteralPath (Join-Path $releasePath 'web.config') -Raw
        $aspNetCore = $webConfig.SelectSingleNode('/configuration/location/system.webServer/aspNetCore')
        if ($null -eq $aspNetCore) { throw '发布包 web.config 缺少 aspNetCore 节点。' }
        $environmentVariables = $aspNetCore.SelectSingleNode('environmentVariables')
        if ($null -eq $environmentVariables) {
            $environmentVariables = $webConfig.CreateElement('environmentVariables')
            [void]$aspNetCore.AppendChild($environmentVariables)
        }
        $existingVariable = $environmentVariables.SelectSingleNode("environmentVariable[@name='LABELSYSTEM_CONFIG_PATH']")
        if ($null -eq $existingVariable) {
            $existingVariable = $webConfig.CreateElement('environmentVariable')
            $existingVariable.SetAttribute('name', 'LABELSYSTEM_CONFIG_PATH')
            [void]$environmentVariables.AppendChild($existingVariable)
        }
        $existingVariable.SetAttribute('value', $configPath)
        $webConfig.Save((Join-Path $releasePath 'web.config'))

        if (Test-Path "IIS:\Sites\$siteName") { Stop-Website -Name $siteName }
        if (Test-Path "IIS:\AppPools\$poolName") {
            Stop-WebAppPool -Name $poolName -ErrorAction SilentlyContinue
        }

        if (Test-Path -LiteralPath $newConfigPath) {
            Move-Item -LiteralPath $newConfigPath -Destination $configPath -Force
        }
        if (-not (Test-Path "IIS:\AppPools\$poolName")) {
            New-WebAppPool -Name $poolName | Out-Null
        }
        Set-ItemProperty "IIS:\AppPools\$poolName" managedRuntimeVersion ''
        Set-ItemProperty "IIS:\AppPools\$poolName" managedPipelineMode 'Integrated'
        Set-ItemProperty "IIS:\AppPools\$poolName" enable32BitAppOnWin64 $false
        Set-ItemProperty "IIS:\AppPools\$poolName" startMode 'AlwaysRunning'
        Set-ItemProperty "IIS:\AppPools\$poolName" processModel.idleTimeout ([TimeSpan]::Zero)
        Set-ItemProperty "IIS:\AppPools\$poolName" processModel.loadUserProfile $true

        if (-not (Test-Path "IIS:\Sites\$siteName")) {
            New-Website -Name $siteName -PhysicalPath $releasePath -Port $RequestedPort `
                -IPAddress '*' -ApplicationPool $poolName | Out-Null
        }
        else {
            Set-ItemProperty "IIS:\Sites\$siteName" physicalPath $releasePath
            Set-ItemProperty "IIS:\Sites\$siteName" applicationPool $poolName
        }
        Set-ItemProperty "IIS:\Sites\$siteName" applicationDefaults.preloadEnabled $true

        & icacls $releasePath /grant "IIS AppPool\${poolName}:(OI)(CI)(RX)" /T /Q | Out-Null
        & icacls $archivePath /grant "IIS AppPool\${poolName}:(OI)(CI)(M)" /T /Q | Out-Null
        & icacls $configPath /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' "IIS AppPool\${poolName}:(R)" /Q | Out-Null

        Start-WebAppPool -Name $poolName
        Start-Website -Name $siteName
        Start-Sleep -Seconds 2
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$RequestedPort/api/v1/health" -UseBasicParsing
        [pscustomobject]@{
            Site = (Get-Website -Name $siteName).State
            Pool = (Get-WebAppPoolState -Name $poolName).Value
            Binding = (Get-WebBinding -Name $siteName).bindingInformation
            ReleasePath = $releasePath
            HealthStatus = $health.data.status
            Environment = $health.data.environment
            Version = $health.data.version
            DatabaseReady = $health.data.databaseReady
            U8WritesEnabled = $health.data.u8WritesEnabled
            ExistingSites = @(Get-Website | Select-Object -ExpandProperty Name)
        }
    } -ArgumentList $Port
}
finally {
    Remove-PSSession $session
}
