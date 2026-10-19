param([string]$ManagementHost='172.16.1.159')
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
# Obtain credentials in memory via the existing administrative transport; never emit them.
$connectionText=Invoke-Command -ComputerName $ManagementHost -Authentication Negotiate -ScriptBlock {
    $ErrorActionPreference='Stop'
    $config=Get-Content -LiteralPath 'D:\LabelSystem\Config\label-api.json' -Raw | ConvertFrom-Json
    $builder=[System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$config.ConnectionStrings.LabelSystem)
    $builder['Initial Catalog']='master'
    $builder['Application Name']='Label Print Isolated Acceptance Bootstrap'
    $builder.ConnectionString
}
$oldMaster=$env:PRINT_ACCEPTANCE_MASTER
$oldRepo=$env:PRINT_ACCEPTANCE_REPO
try {
    $env:PRINT_ACCEPTANCE_MASTER=$connectionText
    $env:PRINT_ACCEPTANCE_REPO=$repoRoot
    & dotnet run --project (Join-Path $PSScriptRoot 'PrintAcceptance.csproj') --no-build
    if($LASTEXITCODE -ne 0){throw 'Isolated acceptance failed; see sanitized result.json.'}
} finally {
    $env:PRINT_ACCEPTANCE_MASTER=$oldMaster
    $env:PRINT_ACCEPTANCE_REPO=$oldRepo
    $connectionText=$null
}
