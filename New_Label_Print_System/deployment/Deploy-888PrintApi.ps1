[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedApiPath,
    [string]$ManagementHost = '172.16.1.159'
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $PublishedApiPath).Path
$stage = Join-Path $env:TEMP ('label-print-api-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$archive = Join-Path $stage 'api.zip'
try {
    Compress-Archive -Path (Join-Path $source '*') -DestinationPath $archive -Force
    $session = New-PSSession -ComputerName $ManagementHost -Authentication Negotiate
    try {
        Invoke-Command -Session $session -ScriptBlock {
            $ErrorActionPreference = 'Stop'
            Import-Module WebAdministration
            $configPath = 'D:\LabelSystem\DevPrinting\Config\label-api.dev.json'
            $cfg = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            $db = New-Object System.Data.SqlClient.SqlConnectionStringBuilder(
                [string]$cfg.ConnectionStrings.LabelSystem)
            if ($cfg.LabelSystem.Environment -ne 'development' -or
                $cfg.LabelSystem.ErpAccountId -ne '888' -or
                $cfg.LabelSystem.ErpDatabaseName -ne 'UFDATA_888_2021' -or
                $db['Initial Catalog'] -ne 'LabelSystem_Dev') {
                throw 'Exact development 888 boundary required.'
            }
            if (-not ((Get-WebBinding -Name 'LabelSystemDevPrint').bindingInformation -match ':9891:')) {
                throw 'Unexpected development API binding.'
            }
        }
        $incoming = 'D:\LabelSystem\DevPrinting\incoming\print-api.zip'
        Copy-Item -LiteralPath $archive -Destination $incoming -ToSession $session -Force
        Invoke-Command -Session $session -ScriptBlock {
            $ErrorActionPreference = 'Stop'
            Import-Module WebAdministration
            $stamp = Get-Date -Format yyyyMMdd_HHmmss
            $configPath = 'D:\LabelSystem\DevPrinting\Config\label-api.dev.json'
            $cfg = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            $oldApi = (Get-Website -Name 'LabelSystemDevPrint').physicalPath
            $release = "D:\LabelSystem\DevPrinting\releases\${stamp}_print_api\api"
            $backup = "D:\LabelSystem\DevPrinting\backups\${stamp}_print_api"
            New-Item -ItemType Directory -Path $release,$backup -Force | Out-Null
            Copy-Item -LiteralPath $configPath -Destination (Join-Path $backup 'label-api.dev.json')
            [IO.File]::WriteAllText(
                (Join-Path $backup 'previous-path.txt'),
                $oldApi,
                (New-Object Text.UTF8Encoding($false)))
            Expand-Archive -LiteralPath 'D:\LabelSystem\DevPrinting\incoming\print-api.zip' `
                -DestinationPath $release
            Copy-Item -LiteralPath (Join-Path $oldApi 'web.config') `
                -Destination (Join-Path $release 'web.config') -Force
            foreach ($entry in @{
                SimulationOnly = $false
                SourceEnabled = $true
                AllowNewDispatch = $true
                RecoveryEnabled = $false
            }.GetEnumerator()) {
                $cfg.Printing | Add-Member NoteProperty $entry.Key $entry.Value -Force
            }
            [IO.File]::WriteAllText(
                $configPath,
                ($cfg | ConvertTo-Json -Depth 20),
                (New-Object Text.UTF8Encoding($false)))
            Stop-WebAppPool -Name 'LabelSystemDevPrintPool'
            Set-ItemProperty 'IIS:\Sites\LabelSystemDevPrint' -Name physicalPath -Value $release
            Start-WebAppPool -Name 'LabelSystemDevPrintPool'
            [pscustomobject]@{
                url = 'http://172.16.1.159:9891'
                account = '888'
                release = $release
                backup = $backup
            }
        }
    } finally {
        if ($session) { Remove-PSSession $session }
    }
} finally {
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP + [IO.Path]::DirectorySeparatorChar)
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if (-not $resolvedStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected API staging path.'
    }
    if (Test-Path -LiteralPath $resolvedStage) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
