[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$LabelSystemRoot,
 [string]$ManagementHost='172.16.1.159'
)
$ErrorActionPreference='Stop'
throw '888模拟运行模式已停用。正常部署请使用Enable-888NormalPrinting.ps1；本脚本仅保留历史部署记录。'
$repo=Split-Path $PSScriptRoot -Parent
$stage=Join-Path $LabelSystemRoot '.tools\new-label-standalone\simulation-deploy'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$api=Join-Path $LabelSystemRoot '.tools\new-label-standalone\simulation-api'
if(-not(Test-Path (Join-Path $api 'LabelSystem.Api.dll'))){throw 'Build the simulation API first'}
Compress-Archive -Path (Join-Path $api '*') -DestinationPath (Join-Path $stage 'api.zip') -Force
& (Join-Path $repo 'backend\.venv\Scripts\python.exe') (Join-Path $PSScriptRoot 'build_package.py') --output (Join-Path $stage 'application.zip') --application-only
if($LASTEXITCODE -ne 0){throw 'Application package failed'}
$session=New-PSSession -ComputerName $ManagementHost -Authentication Negotiate
try {
 $safe=Invoke-Command -Session $session -ScriptBlock {
  Import-Module WebAdministration
  $cfg=Get-Content 'D:\LabelSystem\DevPrinting\Config\label-api.dev.json' -Raw|ConvertFrom-Json
  $db=New-Object System.Data.SqlClient.SqlConnectionStringBuilder([string]$cfg.ConnectionStrings.LabelSystem)
  if($cfg.LabelSystem.Environment -ne 'development' -or $cfg.LabelSystem.ErpAccountId -ne '888' -or $cfg.LabelSystem.ErpDatabaseName -ne 'UFDATA_888_2021' -or $db['Initial Catalog'] -ne 'LabelSystem_Dev'){throw 'Exact 888 development boundary required'}
  foreach($pair in @(@('LabelSystemDevPrint','9891'),@('U8GatewayDevPrint','9881'))){
   if(-not((Get-WebBinding -Name $pair[0]).bindingInformation -match (':'+$pair[1]+':'))){throw 'Wrong development binding'}
  }
  $own=Get-Content 'D:\NewLabelPrintDevelopment\Config\standalone.json' -Raw|ConvertFrom-Json
  if($own.u8Database -ne 'UFDATA_888_2021' -or $own.port -ne 18003){throw 'Wrong independent application'}
  '888 development preflight passed'
 }
 Write-Output $safe
 foreach($name in @('api.zip','application.zip')){Copy-Item -LiteralPath (Join-Path $stage $name) -Destination ('D:\LabelSystem\DevPrinting\incoming\simulation-'+$name) -ToSession $session -Force}
 Copy-Item -LiteralPath (Join-Path $LabelSystemRoot '.tools\new-label-standalone\mock-helper-test.exe') -Destination 'D:\LabelSystem\DevPrinting\incoming\simulation-mock-helper.exe' -ToSession $session -Force
 Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Set-888GatewaySimulationWrites.ps1') -Destination 'D:\LabelSystem\DevPrinting\incoming\Set-888GatewaySimulationWrites.ps1' -ToSession $session -Force
 $result=Invoke-Command -Session $session -ScriptBlock {
  Import-Module WebAdministration
  $stamp=Get-Date -Format yyyyMMdd_HHmmss
  $backup='D:\NewLabelPrintDevelopment\backups\simulation-'+$stamp
  New-Item -ItemType Directory -Path $backup -Force|Out-Null
  $configPath='D:\LabelSystem\DevPrinting\Config\label-api.dev.json'
  $ownPath='D:\NewLabelPrintDevelopment\Config\standalone.json'
  $cfg=Get-Content $configPath -Raw|ConvertFrom-Json
  $own=Get-Content $ownPath -Raw|ConvertFrom-Json
  $gateway=(Get-Website -Name U8GatewayDevPrint).physicalPath
  $wow64=Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
  $gatewayScript='D:\LabelSystem\DevPrinting\incoming\Set-888GatewaySimulationWrites.ps1'
  $null=& $wow64 -NoProfile -ExecutionPolicy Bypass -File $gatewayScript -GatewayDirectory $gateway -ValidateOnly
  if($LASTEXITCODE -ne 0){throw 'Gateway development preflight failed'}
  Copy-Item -LiteralPath $configPath -Destination (Join-Path $backup 'label-api.dev.json')
  Copy-Item -LiteralPath $ownPath -Destination (Join-Path $backup 'standalone.json')
  Copy-Item -LiteralPath (Join-Path $gateway 'App_Data\settings.json') -Destination (Join-Path $backup 'gateway-settings.json')
  & icacls $backup /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-19:(OI)(CI)F' /Q|Out-Null
  $sim='D:\NewLabelPrintDevelopment\Simulation'
  New-Item -ItemType Directory -Path $sim -Force|Out-Null
  & icacls $sim /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-19:(OI)(CI)F' 'IIS AppPool\LabelSystemDevPrintPool:(OI)(CI)M' /Q|Out-Null
  if($LASTEXITCODE -ne 0){throw 'Simulation directory permissions failed'}
  $pairPath=Join-Path $sim 'device.json'
  $keyPath=Join-Path $sim 'device.key'
  if(-not(Test-Path -LiteralPath $pairPath)){
   if(Test-Path -LiteralPath $keyPath){throw 'Restore existing simulation identity; do not rotate it'}
   $start=New-Object Diagnostics.ProcessStartInfo
   $start.FileName=Join-Path (Split-Path $own.backendDirectory -Parent) 'connector\dist\blg-connector-windows.exe'
   $start.Arguments='-init-v2 "'+$keyPath+'"'
   $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.StandardOutputEncoding=[Text.Encoding]::UTF8
   $process=[Diagnostics.Process]::Start($start);$identity=$process.StandardOutput.ReadToEnd();$process.WaitForExit()
   if($process.ExitCode -ne 0){throw 'Simulation identity initialization failed'}
   $null=$identity|ConvertFrom-Json
   [IO.File]::WriteAllText($pairPath,$identity,(New-Object Text.UTF8Encoding($false)))
  }
  $pair=Get-Content -LiteralPath $pairPath -Raw -Encoding UTF8|ConvertFrom-Json
  $sha=[Security.Cryptography.SHA256]::Create()
  $hash=([BitConverter]::ToString($sha.ComputeHash([Convert]::FromBase64String($pair.publicKey)))).Replace('-','')
  $api='D:\LabelSystem\DevPrinting\releases\'+$stamp+'_simulation\api'
  Expand-Archive -LiteralPath 'D:\LabelSystem\DevPrinting\incoming\simulation-api.zip' -DestinationPath $api
  $oldApi=(Get-Website -Name LabelSystemDevPrint).physicalPath
  Copy-Item -LiteralPath (Join-Path $oldApi 'web.config') -Destination (Join-Path $api 'web.config') -Force
  $release='D:\NewLabelPrintDevelopment\releases\'+$stamp+'_simulation'
  Expand-Archive -LiteralPath 'D:\LabelSystem\DevPrinting\incoming\simulation-application.zip' -DestinationPath $release
  $oldRelease=Split-Path $own.backendDirectory -Parent
  [IO.File]::WriteAllText((Join-Path $backup 'previous-paths.json'),(@{api=$oldApi;independent=$oldRelease}|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
  # Do not replace keys, evidence ledgers, or technical application data.
  Stop-ScheduledTask -TaskName NewLabelPrintDevelopment
  Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -eq $own.pythonPath -and $_.CommandLine -match 'standalone_labelsystem\.py'}|ForEach-Object {Stop-Process -Id $_.ProcessId -Force}
  Get-CimInstance Win32_Process -Filter "Name='simulation-mock-helper.exe'"|Where-Object {$_.ExecutablePath -eq (Join-Path $sim 'simulation-mock-helper.exe')}|ForEach-Object {Stop-Process -Id $_.ProcessId -Force}
  Copy-Item -LiteralPath 'D:\LabelSystem\DevPrinting\incoming\simulation-mock-helper.exe' -Destination (Join-Path $sim 'simulation-mock-helper.exe') -Force
  Stop-WebAppPool -Name LabelSystemDevPrintPool
  $cfg.LabelSystem.U8WritesEnabled=$true
  $cfg.Printing.AllowNewDispatch=$true
  $cfg.Printing.RecoveryEnabled=$false
  $sourceKey=[string]$own.printSourceSigningKey
  if($sourceKey.Length -lt 32){$rng=[Security.Cryptography.RandomNumberGenerator]::Create();$bytes=New-Object byte[] 32;$rng.GetBytes($bytes);$rng.Dispose();$sourceKey=([BitConverter]::ToString($bytes)).Replace('-','')}
  $cfg.Printing|Add-Member -MemberType NoteProperty -Name SourceVerificationKey -Value $sourceKey -Force
  $own|Add-Member -MemberType NoteProperty -Name printSourceSigningKey -Value $sourceKey -Force
  foreach($item in @{SimulationOnly=$true;SimulationDeviceId=$pair.id;SimulationDeviceKeyHash=$hash;SimulationStatePath=$sim}.GetEnumerator()){$cfg.Printing|Add-Member -MemberType NoteProperty -Name $item.Key -Value $item.Value -Force}
  [IO.File]::WriteAllText($configPath,($cfg|ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding($false)))
  $null=& $wow64 -NoProfile -ExecutionPolicy Bypass -File $gatewayScript -GatewayDirectory $gateway
  if($LASTEXITCODE -ne 0){throw 'Gateway development configuration failed'}
  Stop-WebAppPool -Name U8GatewayDevPrintPool
  Set-ItemProperty 'IIS:\Sites\LabelSystemDevPrint' -Name physicalPath -Value $api
  $own.backendDirectory=Join-Path $release 'backend'
  $own|Add-Member -MemberType NoteProperty -Name printSimulation -Value @{statePath=$sim;binaryPath=(Join-Path $sim 'simulation-mock-helper.exe');privateKeyPath=$keyPath;identityPath=$pairPath} -Force
  [IO.File]::WriteAllText($ownPath,($own|ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding($false)))
  foreach($name in @('U8GatewayDevPrintPool','LabelSystemDevPrintPool')){
   for($attempt=0;$attempt -lt 40;$attempt++){
    $state=(Get-WebAppPoolState -Name $name).Value
    if($state -eq 'Started'){break}
    if($state -eq 'Stopped'){try{Start-WebAppPool -Name $name}catch{}}
    Start-Sleep -Milliseconds 250
   }
   if((Get-WebAppPoolState -Name $name).Value -ne 'Started'){throw ('Development pool did not start: '+$name)}
  }
  Start-ScheduledTask -TaskName NewLabelPrintDevelopment
  [pscustomobject]@{url='http://172.16.1.159:18003/bills';account='888';simulationOnly=$true;deviceId=$pair.id;api=$api;release=$release;backup=$backup;automaticRecovery=$false}
 }
 $result|Select-Object url,account,simulationOnly,deviceId,api,release,backup,automaticRecovery|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'deployment.json') -Encoding utf8
 $result|Select-Object url,account,simulationOnly,deviceId,api,release,automaticRecovery
} finally {Remove-PSSession $session}
