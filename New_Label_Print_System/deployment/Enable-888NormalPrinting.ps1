[CmdletBinding()]
param([Parameter(Mandatory)][string]$LabelSystemRoot,[string]$ManagementHost='172.16.1.159')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$stage=Join-Path $LabelSystemRoot '.tools\new-label-standalone\normal-deploy'
New-Item -ItemType Directory -Path $stage -Force|Out-Null
Compress-Archive -Path (Join-Path $LabelSystemRoot '.tools\new-label-standalone\normal-api\*') -DestinationPath (Join-Path $stage 'api.zip') -Force
& (Join-Path $repo 'backend\.venv\Scripts\python.exe') (Join-Path $PSScriptRoot 'build_package.py') --application-only --output (Join-Path $stage 'application.zip')
if($LASTEXITCODE -ne 0){throw 'Application package failed'}
$session=New-PSSession -ComputerName $ManagementHost -Authentication Negotiate
try {
 Invoke-Command -Session $session -ScriptBlock {
  $ErrorActionPreference='Stop';Import-Module WebAdministration
  $cfg=Get-Content 'D:\LabelSystem\DevPrinting\Config\label-api.dev.json' -Raw|ConvertFrom-Json
  $db=New-Object System.Data.SqlClient.SqlConnectionStringBuilder([string]$cfg.ConnectionStrings.LabelSystem)
  $own=Get-Content 'D:\NewLabelPrintDevelopment\Config\standalone.json' -Raw|ConvertFrom-Json
  if($cfg.LabelSystem.Environment -ne 'development' -or $cfg.LabelSystem.ErpAccountId -ne '888' -or $cfg.LabelSystem.ErpDatabaseName -ne 'UFDATA_888_2021' -or $db['Initial Catalog'] -ne 'LabelSystem_Dev' -or $own.u8Database -ne 'UFDATA_888_2021' -or $own.port -ne 18003){throw 'Exact development 888 boundary required'}
  if(-not((Get-WebBinding -Name LabelSystemDevPrint).bindingInformation -match ':9891:')){throw 'Wrong API site'}
  if([string]$own.printSourceSigningKey -ne [string]$cfg.Printing.SourceVerificationKey -or ([string]$own.printSourceSigningKey).Length -lt 32){throw 'Existing source signing key required'}
  'Development 888 preflight passed'
 }
 foreach($name in @('api.zip','application.zip')){Copy-Item -LiteralPath (Join-Path $stage $name) -Destination ('D:\LabelSystem\DevPrinting\incoming\normal-'+$name) -ToSession $session -Force}
 $result=Invoke-Command -Session $session -ScriptBlock {
  $ErrorActionPreference='Stop';Import-Module WebAdministration
  $stamp=Get-Date -Format yyyyMMdd_HHmmss
  $backup='D:\NewLabelPrintDevelopment\backups\normal-'+$stamp
  New-Item -ItemType Directory -Path $backup -Force|Out-Null
  & icacls $backup /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-19:(OI)(CI)F' /Q|Out-Null
  if($LASTEXITCODE -ne 0){throw 'Backup permissions failed'}
  $configPath='D:\LabelSystem\DevPrinting\Config\label-api.dev.json'
  $ownPath='D:\NewLabelPrintDevelopment\Config\standalone.json'
  $cfg=Get-Content $configPath -Raw|ConvertFrom-Json;$own=Get-Content $ownPath -Raw|ConvertFrom-Json
  Copy-Item -LiteralPath $configPath -Destination (Join-Path $backup 'label-api.dev.json')
  Copy-Item -LiteralPath $ownPath -Destination (Join-Path $backup 'standalone.json')
  $oldApi=(Get-Website -Name LabelSystemDevPrint).physicalPath
  $api='D:\LabelSystem\DevPrinting\releases\'+$stamp+'_normal\api'
  $release='D:\NewLabelPrintDevelopment\releases\'+$stamp+'_normal'
  Expand-Archive -LiteralPath 'D:\LabelSystem\DevPrinting\incoming\normal-api.zip' -DestinationPath $api
  Copy-Item -LiteralPath (Join-Path $oldApi 'web.config') -Destination (Join-Path $api 'web.config') -Force
  Expand-Archive -LiteralPath 'D:\LabelSystem\DevPrinting\incoming\normal-application.zip' -DestinationPath $release
  [IO.File]::WriteAllText((Join-Path $backup 'previous-paths.json'),(@{api=$oldApi;independent=(Split-Path $own.backendDirectory -Parent)}|ConvertTo-Json))
  Stop-ScheduledTask -TaskName NewLabelPrintDevelopment
  Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -eq $own.pythonPath -and $_.CommandLine -match 'standalone_labelsystem\.py'}|ForEach-Object {Stop-Process -Id $_.ProcessId -Force}
  Get-CimInstance Win32_Process -Filter "Name='simulation-mock-helper.exe'"|Where-Object {$_.ExecutablePath -eq 'D:\NewLabelPrintDevelopment\Simulation\simulation-mock-helper.exe'}|ForEach-Object {Stop-Process -Id $_.ProcessId -Force}
  Stop-WebAppPool -Name LabelSystemDevPrintPool
  foreach($entry in @{SimulationOnly=$false;SourceEnabled=$true;AllowNewDispatch=$true;RecoveryEnabled=$false}.GetEnumerator()){$cfg.Printing|Add-Member NoteProperty $entry.Key $entry.Value -Force}
  $own.PSObject.Properties.Remove('printSimulation')
  $own.backendDirectory=Join-Path $release 'backend'
  [IO.File]::WriteAllText($configPath,($cfg|ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding($false)))
  [IO.File]::WriteAllText($ownPath,($own|ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding($false)))
  Set-ItemProperty 'IIS:\Sites\LabelSystemDevPrint' -Name physicalPath -Value $api
  for($i=0;$i -lt 40;$i++){
   $state=(Get-WebAppPoolState -Name LabelSystemDevPrintPool).Value
   if($state -eq 'Started'){break}
   if($state -eq 'Stopped'){Start-WebAppPool -Name LabelSystemDevPrintPool}
   Start-Sleep -Milliseconds 250
  }
  if((Get-WebAppPoolState -Name LabelSystemDevPrintPool).Value -ne 'Started'){throw 'Development API failed to start'}
  Start-ScheduledTask -TaskName NewLabelPrintDevelopment
  [pscustomobject]@{url='http://172.16.1.159:18003/bills';account='888';simulationOnly=$false;sourceEnabled=$true;release=$release;api=$api;backup=$backup;automaticRecovery=$false}
 }
 $result|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'deployment.json') -Encoding utf8
 $result|Select-Object url,account,simulationOnly,sourceEnabled,release,api
} finally {Remove-PSSession $session}
