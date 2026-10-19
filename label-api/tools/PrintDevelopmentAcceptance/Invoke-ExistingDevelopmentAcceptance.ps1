[CmdletBinding()]
param([string]$ManagementHost='172.16.1.159')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$run=[guid]::NewGuid().ToString('N').Substring(0,12)
$work=Join-Path $repo ('.tools\print-development-acceptance\'+$run)
New-Item -ItemType Directory -Path $work -Force | Out-Null
$owner=[Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls $work /inheritance:r /grant:r "$($owner):(OI)(CI)(F)" '*S-1-5-18:(OI)(CI)(F)' '*S-1-5-32-544:(OI)(CI)(F)' /Q | Out-Null
if($LASTEXITCODE -ne 0){throw 'Private acceptance directory ACL failed'}
$baseline=Get-Content -LiteralPath (Join-Path $repo '.tools\print-dev-deploy\production-baseline.json') -Raw | ConvertFrom-Json
$settings=Invoke-Command -ComputerName $ManagementHost -Authentication Negotiate -ScriptBlock {
    $cfg=Get-Content -LiteralPath 'D:\LabelSystem\DevPrinting\Config\label-api.dev.json' -Raw | ConvertFrom-Json
    if($cfg.LabelSystem.ErpAccountId -ne '888' -or $cfg.LabelSystem.U8WritesEnabled -or $cfg.Printing.AllowNewDispatch -or $cfg.Printing.RecoveryEnabled){throw 'Existing public development gates must remain closed'}
    $b=New-Object System.Data.SqlClient.SqlConnectionStringBuilder([string]$cfg.ConnectionStrings.LabelSystem)
    if($b['Initial Catalog'] -ne 'LabelSystem_Dev'){throw 'Existing development database required'}
    if($b.DataSource -in @('.','localhost','127.0.0.1','(local)')){$b.DataSource='172.16.1.159'}
    $b['Application Name']='Explicit mock-only development print acceptance'
    $label=$b.ConnectionString;$b['Initial Catalog']='UFDATA_888_2021'
    # Returned only into private process memory, never printed or serialized as evidence.
    [pscustomobject]@{label=$label;u8=$b.ConnectionString}
}
function Read-PublicProtection {
    $protection=Invoke-Command -ComputerName $ManagementHost -Authentication Negotiate -ArgumentList (,$baseline.protectedFiles) -ScriptBlock {
        param($files)
        $cfg=Get-Content -LiteralPath 'D:\LabelSystem\DevPrinting\Config\label-api.dev.json' -Raw | ConvertFrom-Json
        $hashes=@(foreach($file in $files){[pscustomobject]@{path=$file.path;unchanged=((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -eq $file.sha256)}})
        [pscustomobject]@{account=$cfg.LabelSystem.ErpAccountId;allowNewDispatch=$cfg.Printing.AllowNewDispatch;u8Writes=$cfg.LabelSystem.U8WritesEnabled;recovery=$cfg.Printing.RecoveryEnabled;productionFiles=$hashes}
    }
    if($protection.account -ne '888' -or $protection.allowNewDispatch -or $protection.u8Writes -or $protection.recovery -or @($protection.productionFiles|Where-Object {-not $_.unchanged}).Count){throw 'Public gates or protected production files changed'}
    return ($protection|Select-Object account,allowNewDispatch,u8Writes,recovery,productionFiles)
}
Read-PublicProtection | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'public-before.json') -Encoding utf8
& dotnet build (Join-Path $PSScriptRoot 'PrintDevelopmentAcceptance.csproj') --verbosity quiet
if($LASTEXITCODE -ne 0){throw 'Development API runner build failed'}
& dotnet build (Join-Path $repo 'u8-gateway\tools\PrintDevelopmentAcceptanceBridge') --verbosity quiet
if($LASTEXITCODE -ne 0){throw 'Development TraceBack bridge build failed'}
$go=Join-Path $repo '.tools\go1.27.2\go\bin\go.exe'
Push-Location (Join-Path $repo 'New_Label_Print_System\connector')
try {& $go test -c -o (Join-Path $work 'product-agent-test.exe');if($LASTEXITCODE -ne 0){throw 'Mock-only product helper build failed'}}finally{Pop-Location}
$old=@{}
foreach($name in @('PRINT_DEV_LABEL','PRINT_DEV_U8','PRINT_DEV_RUN','PRINT_DEV_REPO','PRINT_DEV_EXISTING_888')){$old[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$code=1
try {
    $env:PRINT_DEV_LABEL=$settings.label;$env:PRINT_DEV_U8=$settings.u8;$env:PRINT_DEV_RUN=$run;$env:PRINT_DEV_REPO=$repo;$env:PRINT_DEV_EXISTING_888='EXPLICIT_MOCK_ONLY'
    Push-Location $repo
    try {
        & (Join-Path $repo 'New_Label_Print_System\backend\.venv\Scripts\python.exe') (Join-Path $repo 'New_Label_Print_System\deployment\run_development_sql_acceptance.py')
        $code=$LASTEXITCODE
    }finally{Pop-Location}
}finally{
    foreach($name in $old.Keys){[Environment]::SetEnvironmentVariable($name,$old[$name],'Process')}
    $settings=$null
    Read-PublicProtection | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'public-after.json') -Encoding utf8
}
Write-Output ('Acceptance run: '+$run+'; result: '+$code+'; public development gates and production protection unchanged.')
if($code -ne 0){throw ('Acceptance failed; inspect private run '+$run+' and retained fixture evidence. No broad cleanup is performed.')}
