param([Parameter(Mandatory)][string]$GatewayDirectory,[switch]$ValidateOnly)
$ErrorActionPreference='Stop'
$resolved=[IO.Path]::GetFullPath($GatewayDirectory)
if(-not $resolved.StartsWith('D:\LabelSystem\DevPrinting\releases\',[StringComparison]::OrdinalIgnoreCase)){
 throw 'Only the explicit development gateway may be configured'
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $resolved 'bin\Newtonsoft.Json.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $resolved 'bin\U8Gateway.Persistence.dll'))
$store=New-Object U8Gateway.Persistence.Configuration.ProtectedSettingsStore((Join-Path $resolved 'App_Data\settings.json'))
$settings=$store.Current
if($settings.SqlDatabase -ne 'UFDATA_888_2021' -or $settings.U8AccountId -ne '888' -or $settings.EnableProductionWrites){
 throw 'Gateway is outside development 888'
}
if(-not $ValidateOnly){
 $settings.EnableTestWrites=$true
 $settings.EnableProductionWrites=$false
 $settings.WriteAllowedDatabase='UFDATA_888_2021'
 $settings.WriteAllowedAccountId='888'
 $null=$store.Save($settings,'')
}
'888 gateway boundary verified'
