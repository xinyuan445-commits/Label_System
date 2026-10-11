[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Server,
    [pscredential]$Credential,
    [string]$PackagePath,
    [int]$Port = 9880
)

$ErrorActionPreference = 'Stop'
$gatewayRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $gatewayRoot 'runtime-data\publish\U8Gateway.Api'
}
$packageRoot = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'bin\U8Gateway.Api.dll'))) {
    throw "IIS 发布包无效：$packageRoot"
}

$zipRoot = Join-Path $gatewayRoot 'runtime-data\publish'
New-Item -ItemType Directory -Path $zipRoot -Force | Out-Null
$zipPath = Join-Path $zipRoot 'U8Gateway.Api.zip'
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
            ConflictingBindings = @(
                Get-WebBinding | Where-Object {
                    $_.bindingInformation -match (':' + $RequestedPort + ':') -and
                    $_.ItemXPath -notmatch "site\[@name='U8Gateway'"
                }).Count
            ExistingSites = @(Get-Website | Select-Object -ExpandProperty Name)
        }
    } -ArgumentList $Port
    if ($preflight.ConflictingBindings -gt 0) {
        throw "目标服务器端口 $Port 已被其他 IIS 站点占用。"
    }

    Invoke-Command -Session $session -ScriptBlock {
        New-Item -ItemType Directory -Path 'D:\U8Gateway\incoming' -Force | Out-Null
    }
    Copy-Item -LiteralPath $zipPath `
        -Destination 'D:\U8Gateway\incoming\U8Gateway.Api.zip' `
        -ToSession $session -Force

    Invoke-Command -Session $session -ScriptBlock {
        param($RequestedPort)
        $ErrorActionPreference = 'Stop'
        Import-Module WebAdministration
        $siteName = 'U8Gateway'
        $poolName = 'U8GatewayPool'
        $sitePath = 'D:\U8Gateway\site'
        $stagePath = 'D:\U8Gateway\staging-current'
        $zipPath = 'D:\U8Gateway\incoming\U8Gateway.Api.zip'
        $dataPath = 'D:\U8GatewayData'
        $archivePath = 'D:\U8GatewayData\operation-archive'

        if (Test-Path "IIS:\Sites\$siteName") { Stop-Website -Name $siteName }
        if (Test-Path "IIS:\AppPools\$poolName") {
            Stop-WebAppPool -Name $poolName -ErrorAction SilentlyContinue
        }

        New-Item -ItemType Directory -Path $sitePath -Force | Out-Null
        New-Item -ItemType Directory -Path "$sitePath\App_Data" -Force | Out-Null
        New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
        New-Item -ItemType Directory -Path $archivePath -Force | Out-Null
        Expand-Archive -LiteralPath $zipPath -DestinationPath $stagePath -Force
        Get-ChildItem -LiteralPath $stagePath -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $sitePath -Recurse -Force
        }

        if (-not (Test-Path "IIS:\AppPools\$poolName")) {
            New-WebAppPool -Name $poolName | Out-Null
        }
        Set-ItemProperty "IIS:\AppPools\$poolName" managedRuntimeVersion 'v4.0'
        Set-ItemProperty "IIS:\AppPools\$poolName" managedPipelineMode 'Integrated'
        Set-ItemProperty "IIS:\AppPools\$poolName" enable32BitAppOnWin64 $true
        Set-ItemProperty "IIS:\AppPools\$poolName" startMode 'AlwaysRunning'
        Set-ItemProperty "IIS:\AppPools\$poolName" processModel.loadUserProfile $true

        if (-not (Test-Path "IIS:\Sites\$siteName")) {
            New-Website -Name $siteName -PhysicalPath $sitePath `
                -Port $RequestedPort -IPAddress '*' -ApplicationPool $poolName | Out-Null
        }
        else {
            Set-ItemProperty "IIS:\Sites\$siteName" physicalPath $sitePath
            Set-ItemProperty "IIS:\Sites\$siteName" applicationPool $poolName
        }

        & icacls $sitePath /grant "IIS AppPool\${poolName}:(OI)(CI)(RX)" /T /Q | Out-Null
        & icacls "$sitePath\App_Data" /grant "IIS AppPool\${poolName}:(OI)(CI)(M)" /T /Q | Out-Null
        & icacls $dataPath /grant "IIS AppPool\${poolName}:(OI)(CI)(M)" /T /Q | Out-Null

        Start-WebAppPool -Name $poolName
        Start-Website -Name $siteName
        Start-Sleep -Seconds 2
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$RequestedPort/api/v1/health" -UseBasicParsing
        [pscustomobject]@{
            Site = (Get-Website -Name $siteName).State
            Pool = (Get-WebAppPoolState -Name $poolName).Value
            PhysicalPath = (Get-Website -Name $siteName).PhysicalPath
            Binding = (Get-WebBinding -Name $siteName).bindingInformation
            HealthStatus = $health.data.status
            Configured = $health.data.configured
            Version = $health.data.version
            ExistingSites = @(Get-Website | Select-Object -ExpandProperty Name)
        }
    } -ArgumentList $Port
}
finally {
    Remove-PSSession $session
}
