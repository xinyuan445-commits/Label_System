[CmdletBinding()]
param([string]$Server='172.16.1.159',[Parameter(Mandatory)][string]$PackagePath,
      [Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
# Dedicated development sites only. Do not call the existing production deployment scripts.
$package=[IO.Path]::GetFullPath($PackagePath)
$releaseId=Get-Date -Format 'yyyyMMdd_HHmmss'
$signing=[Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
try{$signingPem=$signing.ExportPkcs8PrivateKeyPem()}finally{$signing.Dispose()}
$session=New-PSSession -ComputerName $Server -Authentication Negotiate
try {
    Invoke-Command -Session $session -ScriptBlock {
        $ErrorActionPreference='Stop';Import-Module WebAdministration
        foreach($port in @(9891,9881,18781)){
            if(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue){throw "Development port occupied: $port"}
        }
        if((Test-Path IIS:\Sites\LabelSystemDevPrint) -or (Test-Path IIS:\Sites\U8GatewayDevPrint)){
            throw 'Development sites already exist; inspect and perform a reviewed update instead.'
        }
        New-Item -ItemType Directory -Path 'D:\LabelSystem\DevPrinting\incoming' -Force | Out-Null
    }
    $packageHash=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    $remoteHash=Invoke-Command -Session $session -ScriptBlock {
        if(Test-Path -LiteralPath 'D:\LabelSystem\DevPrinting\incoming\package.zip'){
            (Get-FileHash -LiteralPath 'D:\LabelSystem\DevPrinting\incoming\package.zip' -Algorithm SHA256).Hash
        }
    }
    if($remoteHash -cne $packageHash){Copy-Item -LiteralPath $package -Destination 'D:\LabelSystem\DevPrinting\incoming\package.zip' -ToSession $session}
    $result=Invoke-Command -Session $session -ArgumentList $releaseId,$signingPem -ScriptBlock {
        param([string]$id,[string]$pem)
        $ErrorActionPreference='Stop';Import-Module WebAdministration
        $root='D:\LabelSystem\DevPrinting'
        $release=Join-Path $root ('releases\'+$id)
        if(Test-Path -LiteralPath $release){throw 'Release path exists'}
        New-Item -ItemType Directory -Path $release -Force | Out-Null
        Expand-Archive -LiteralPath (Join-Path $root 'incoming\package.zip') -DestinationPath $release
        $configRoot=Join-Path $root 'Config';$artifactRoot=Join-Path $root 'printing-artifacts';$archiveRoot=Join-Path $root 'operation-archive'
        foreach($dir in @($configRoot,$artifactRoot,$archiveRoot,(Join-Path $root 'logs'))){New-Item -ItemType Directory -Path $dir -Force|Out-Null}
        $original=Get-Content -LiteralPath 'D:\LabelSystem\Config\label-api.json' -Raw|ConvertFrom-Json
        $sql=[System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$original.ConnectionStrings.LabelSystem)
        $sql['Initial Catalog']='LabelSystem_Dev'
        $c=[System.Data.SqlClient.SqlConnection]::new($sql.ConnectionString)
        try{
            $c.Open();$cmd=$c.CreateCommand();$cmd.CommandText="SELECT DB_NAME(),COUNT(*) FROM dbo.schema_migration WHERE migration_id='017';";$r=$cmd.ExecuteReader();$null=$r.Read()
            if($r.GetString(0) -cne 'LabelSystem_Dev' -or $r.GetInt32(1) -ne 1){throw 'Development migration boundary failed'};$r.Close()
        }finally{$c.Dispose()}
        function New-RandomKey {
            $bytes=New-Object byte[] 32;$random=[Security.Cryptography.RandomNumberGenerator]::Create()
            try{$random.GetBytes($bytes);[Convert]::ToBase64String($bytes)}finally{$random.Dispose()}
        }
        $gatewayKey=New-RandomKey;$rendererKey=New-RandomKey
        $apiPool='LabelSystemDevPrintPool';$gatewayPool='U8GatewayDevPrintPool'
        foreach($pool in @($apiPool,$gatewayPool)){
            if(Test-Path ('IIS:\AppPools\'+$pool)){
                if(@(Get-Website|Where-Object {$_.applicationPool -eq $pool}).Count){throw 'Development pool is already used'}
            }else{New-WebAppPool -Name $pool|Out-Null}
            Set-ItemProperty ('IIS:\AppPools\'+$pool) managedPipelineMode 'Integrated'
            Set-ItemProperty ('IIS:\AppPools\'+$pool) processModel.loadUserProfile $true
            Set-ItemProperty ('IIS:\AppPools\'+$pool) startMode 'AlwaysRunning'
            Set-ItemProperty ('IIS:\AppPools\'+$pool) processModel.idleTimeout ([TimeSpan]::Zero)
        }
        Set-ItemProperty ('IIS:\AppPools\'+$apiPool) managedRuntimeVersion ''
        Set-ItemProperty ('IIS:\AppPools\'+$apiPool) enable32BitAppOnWin64 $false
        Set-ItemProperty ('IIS:\AppPools\'+$gatewayPool) managedRuntimeVersion 'v4.0'
        Set-ItemProperty ('IIS:\AppPools\'+$gatewayPool) enable32BitAppOnWin64 $true
        $signingPath=Join-Path $configRoot 'print-server.pem'
        [IO.File]::WriteAllText($signingPath,$pem,[Text.UTF8Encoding]::new($false))
        & icacls $signingPath /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' "IIS AppPool\${apiPool}:(R)" /Q|Out-Null
        $apiConfigPath=Join-Path $configRoot 'label-api.dev.json'
        $apiConfig=[ordered]@{
            ConnectionStrings=@{LabelSystem=$sql.ConnectionString}
            LabelSystem=@{Environment='development';Version='0.11.0-print-dev';ErpAccountId='888';ErpYear=2021;ErpDatabaseName='UFDATA_888_2021';U8WritesEnabled=$false;EnableDevelopmentLogin=$true}
            U8Gateway=@{BaseUrl='http://127.0.0.1:9881';ApiKey=$gatewayKey}
            Archive=@{RootPath=$archiveRoot}
            Cors=@{AllowedOrigins=@()}
            DevelopmentWorkspace=@{ServeStaticFiles=$true}
            Session=@{CookieName='LabelSystem.DevPrint.Session'}
            Printing=@{Enabled=$true;AllowNewDispatch=$false;RecoveryEnabled=$false;Namespace='labelsystem-dev-888';RootPath=$artifactRoot;SigningKeyPath=$signingPath;RendererUrl='http://127.0.0.1:18781';RendererApiKey=$rendererKey;RendererVersion='label-renderer/1'}
        }
        $apiConfig|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $apiConfigPath -Encoding utf8
        & icacls $apiConfigPath /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' "IIS AppPool\${apiPool}:(R)" /Q|Out-Null
        $gatewayPath=Join-Path $release 'gateway'
        # Match ProtectedSettingsStore's persisted contract without loading its x86 assembly
        # into the 64-bit management process. Existing U8 ciphertext remains machine protected.
        Add-Type -AssemblyName System.Security
        $stored=Get-Content -LiteralPath 'D:\U8Gateway\site\App_Data\settings.json' -Raw|ConvertFrom-Json
        $settings=$stored.Public
        $settings.EnvironmentName='test';$settings.SqlServer=$sql.DataSource;$settings.SqlDatabase='UFDATA_888_2021'
        $settings.SqlUser=$sql.UserID;$settings.SqlPassword=$null
        $settings.U8AccountId='888';$settings.U8Year='2021'
        $settings.EnableTestWrites=$false;$settings.EnableProductionWrites=$false
        $settings.WriteAllowedAccountId='888';$settings.WriteAllowedDatabase='UFDATA_888_2021'
        $settings.InternalApiKey=$null;$settings.OperationArchiveRoot=(Join-Path $root 'gateway-archive')
        $settings.Revision=1
        $salt=New-Object byte[] 32;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($salt);$rng.Dispose()
        $derive=[Security.Cryptography.Rfc2898DeriveBytes]::new((New-RandomKey),$salt,100000,[Security.Cryptography.HashAlgorithmName]::SHA256)
        try{$settings.AdminPasswordHash=[Convert]::ToBase64String($derive.GetBytes(32));$settings.AdminPasswordSalt=[Convert]::ToBase64String($salt)}finally{$derive.Dispose()}
        function Protect-GatewaySecret([string]$value){
            [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($value),
                [Text.Encoding]::UTF8.GetBytes('U8Gateway.Settings.v1'),[Security.Cryptography.DataProtectionScope]::LocalMachine))
        }
        $settingsPath=Join-Path $gatewayPath 'App_Data\settings.json'
        New-Item -ItemType Directory -Path (Split-Path $settingsPath -Parent) -Force|Out-Null
        @{Public=$settings;SqlPasswordProtected=(Protect-GatewaySecret $sql.Password);U8PasswordProtected=$stored.U8PasswordProtected;
          InternalApiKeyProtected=(Protect-GatewaySecret $gatewayKey)}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $settingsPath -Encoding utf8
        & icacls $settingsPath /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' "IIS AppPool\${gatewayPool}:(R)" /Q|Out-Null
        [xml]$gwWeb=Get-Content -LiteralPath (Join-Path $gatewayPath 'Web.config') -Raw
        foreach($item in $gwWeb.configuration.appSettings.add){
            if($item.key -eq 'BusinessApiConfigPath'){$item.SetAttribute('value',[string]$apiConfigPath)}
            if($item.key -eq 'BusinessApiBaseUrl'){$item.SetAttribute('value','http://127.0.0.1:9891')}
        }
        $gwWeb.Save((Join-Path $gatewayPath 'Web.config'))
        $apiPath=Join-Path $release 'api'
        [xml]$apiWeb=Get-Content -LiteralPath (Join-Path $apiPath 'web.config') -Raw
        $asp=$apiWeb.SelectSingleNode('/configuration/location/system.webServer/aspNetCore')
        $variables=$apiWeb.CreateElement('environmentVariables')
        $variable=$apiWeb.CreateElement('environmentVariable');$variable.SetAttribute('name','LABELSYSTEM_CONFIG_PATH');$variable.SetAttribute('value',$apiConfigPath)
        $null=$variables.AppendChild($variable);$null=$asp.AppendChild($variables);$apiWeb.Save((Join-Path $apiPath 'web.config'))
        $rendererConfigPath=Join-Path $configRoot 'renderer.dev.json'
        $rendererConfig=@{ApiKey=$rendererKey;Port=18781;PythonPath=(Join-Path $release 'python\python.exe');WorkingDirectory=(Join-Path $release 'renderer');
            RegularFont=(Join-Path $release 'fonts\msyh.ttc');BoldFont=(Join-Path $release 'fonts\msyhbd.ttc');
            ErrorLog=(Join-Path $root 'logs\renderer-error.log');OutputLog=(Join-Path $root 'logs\renderer-output.log')}
        $rendererConfig|ConvertTo-Json|Set-Content -LiteralPath $rendererConfigPath -Encoding utf8
        & icacls $release /grant "IIS AppPool\${apiPool}:(OI)(CI)(RX)" "IIS AppPool\${gatewayPool}:(OI)(CI)(RX)" '*S-1-5-19:(OI)(CI)(RX)' /T /Q|Out-Null
        foreach($file in @($apiConfigPath,$signingPath)){
            & icacls $file /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' "IIS AppPool\${apiPool}:(R)" /Q|Out-Null
        }
        & icacls $rendererConfigPath /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' '*S-1-5-19:(R)' /Q|Out-Null
        & icacls $settingsPath /inheritance:r /grant:r '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' "IIS AppPool\${gatewayPool}:(R)" /Q|Out-Null
        foreach($dir in @($artifactRoot,$archiveRoot)){
            & icacls $dir /grant "IIS AppPool\${apiPool}:(OI)(CI)(M)" /T /Q|Out-Null
        }
        & icacls (Join-Path $root 'logs') /grant '*S-1-5-19:(OI)(CI)(M)' /T /Q|Out-Null
        $rendererScript=Join-Path $release 'Run-Renderer.ps1'
        $action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$rendererScript+'" -ConfigPath "'+$rendererConfigPath+'"')
        $principal=New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\LOCAL SERVICE' -LogonType ServiceAccount -RunLevel Limited
        $taskSettings=New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
        $null=Register-ScheduledTask -TaskName 'LabelSystemDevPrintRenderer' -Action $action -Principal $principal -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings $taskSettings
        Start-ScheduledTask -TaskName 'LabelSystemDevPrintRenderer'
        $null=New-Website -Name 'U8GatewayDevPrint' -PhysicalPath $gatewayPath -Port 9881 -IPAddress '127.0.0.1' -ApplicationPool $gatewayPool
        $null=New-Website -Name 'LabelSystemDevPrint' -PhysicalPath $apiPath -Port 9891 -IPAddress '*' -ApplicationPool $apiPool
        $null=New-NetFirewallRule -Name 'LabelSystemDevPrint9891' -DisplayName 'Label System development printing' -Protocol TCP -LocalPort 9891 -Direction Inbound -Action Allow -Profile Domain,Private -RemoteAddress LocalSubnet
        $c=[System.Data.SqlClient.SqlConnection]::new($sql.ConnectionString)
        try{$c.Open();$cmd=$c.CreateCommand();$cmd.CommandText="UPDATE dbo.system_function SET is_enabled=1 WHERE function_code LIKE 'print.%'; SELECT COUNT(*) FROM dbo.system_function WHERE function_code LIKE 'print.%' AND is_enabled=1;";$functions=[int]$cmd.ExecuteScalar()}finally{$c.Dispose()}
        [PSCustomObject]@{release=$release;apiUrl='http://172.16.1.159:9891';workspaceUrl='http://172.16.1.159:9891/printing/';database='LabelSystem_Dev';account='888';u8Database='UFDATA_888_2021';gatewayUrl='http://127.0.0.1:9881';printFunctions=$functions;allowNewDispatch=$false;apiWrites=$false;gatewayWrites=$false;rendererTask='LabelSystemDevPrintRenderer'}|ConvertTo-Json -Compress
    }
    New-Item -ItemType Directory -Path $EvidenceDirectory -Force|Out-Null
    $result|Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'deployment-result.json') -Encoding utf8
    $result
} finally {Remove-PSSession $session}
