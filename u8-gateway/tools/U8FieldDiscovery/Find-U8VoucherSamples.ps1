param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$SettingsPath = "D:\MaterialOutboundApi\App_Data\settings.json",

    [string]$Database = "UFDATA_888_2021"
)

$ErrorActionPreference = "Stop"

if ($Database -notmatch '^UFDATA_[0-9]{3}_[0-9]{4}$') {
    throw "The account database name is invalid."
}

function Unprotect-Text {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    Add-Type -AssemblyName System.Security
    $entropy = [Text.Encoding]::UTF8.GetBytes("MaterialOutboundApi.Settings.v1")
    $plain = [Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String($Value),
        $entropy,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [Text.Encoding]::UTF8.GetString($plain)
}

$stored = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 |
    ConvertFrom-Json
$password = Unprotect-Text ([string]$stored.SqlPasswordProtected)
$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$builder["Data Source"] = [string]$stored.Public.SqlServer
$builder["Initial Catalog"] = $Database
$builder["User ID"] = [string]$stored.Public.SqlUser
$builder["Password"] = $password
$builder["Encrypt"] = $false
$builder["TrustServerCertificate"] = $true
$builder["Connect Timeout"] = 15

$queries = @(
    @{ documentType = "purchase-arrival"; sql = "select top (10) ID voucherId,cCode voucherCode,cBusType businessType from dbo.PU_ArrivalVouch where isnull(cBusType,'')<>N'委外加工' order by ID desc" },
    @{ documentType = "subcontract-arrival"; sql = "select top (10) ID voucherId,cCode voucherCode,cBusType businessType from dbo.PU_ArrivalVouch where cBusType=N'委外加工' order by ID desc" },
    @{ documentType = "purchase-receipt"; sql = "select top (10) ID voucherId,cCode voucherCode,cast(null as nvarchar(1)) businessType from dbo.RdRecord01 order by ID desc" },
    @{ documentType = "sales-delivery"; sql = "select top (10) DLID voucherId,cDLCode voucherCode,cBusType businessType from dbo.DispatchList order by DLID desc" },
    @{ documentType = "sales-outbound"; sql = "select top (10) ID voucherId,cCode voucherCode,cast(null as nvarchar(1)) businessType from dbo.RdRecord32 order by ID desc" },
    @{ documentType = "material-outbound"; sql = "select top (10) ID voucherId,cCode voucherCode,cast(null as nvarchar(1)) businessType from dbo.RdRecord11 order by ID desc" },
    @{ documentType = "transfer"; sql = "select top (10) ID voucherId,cTVCode voucherCode,cast(null as nvarchar(1)) businessType from dbo.TransVouch order by ID desc" },
    @{ documentType = "location-adjustment"; sql = "select top (10) ID voucherId,cVouchCode voucherCode,cast(null as nvarchar(1)) businessType from dbo.AdjustPVouch order by ID desc" },
    @{ documentType = "inventory-count"; sql = "select top (10) ID voucherId,cCVCode voucherCode,cast(null as nvarchar(1)) businessType from dbo.CheckVouch order by ID desc" }
)

$connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
$results = @()
$errors = @()
try {
    $connection.Open()
    foreach ($query in $queries) {
        $command = $connection.CreateCommand()
        $command.CommandText = $query.sql
        try {
            $reader = $command.ExecuteReader()
            try {
                while ($reader.Read()) {
                    $results += [ordered]@{
                        documentType = $query.documentType
                        voucherId = [int64]$reader["voucherId"]
                        voucherCode = [string]$reader["voucherCode"]
                        businessType = if ($reader.IsDBNull(2)) { $null } else { [string]$reader["businessType"] }
                    }
                }
            }
            finally {
                $reader.Dispose()
            }
        }
        catch {
            $errors += [ordered]@{
                documentType = $query.documentType
                message = $_.Exception.Message
            }
        }
        finally {
            $command.Dispose()
        }
    }
}
finally {
    $connection.Dispose()
}

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
[ordered]@{
    database = $Database
    readOnly = $true
    collectedAt = (Get-Date).ToString("o")
    samples = $results
    errors = $errors
} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $OutputPath -Encoding UTF8
