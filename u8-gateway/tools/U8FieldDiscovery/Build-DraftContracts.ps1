param(
    [string]$ContractsRoot = (Resolve-Path (
        Join-Path $PSScriptRoot "..\..\document-contracts")).Path
)

$ErrorActionPreference = "Stop"

$registryPath = Join-Path $ContractsRoot "registry.json"
$sourceMapPath = Join-Path $ContractsRoot "u8-source-map.json"
$evidenceRoot = Join-Path $ContractsRoot "evidence"
$registry = Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
$sourceMap = Get-Content -LiteralPath $sourceMapPath -Raw | ConvertFrom-Json
$titles = @{}
foreach ($item in $registry.documents) {
    $titles[$item.documentType] = $item.title
}

foreach ($evidenceFile in Get-ChildItem -LiteralPath $evidenceRoot -Recurse -Filter "*.json") {
    $evidence = Get-Content -LiteralPath $evidenceFile.FullName -Raw |
        ConvertFrom-Json
    $mapping = $sourceMap.documents |
        Where-Object documentType -EQ $evidence.documentType |
        Select-Object -First 1
    if ($null -eq $mapping) {
        throw "缺少 $($evidence.documentType) 的 U8 来源映射。"
    }

    $fields = @()
    foreach ($scope in $evidence.scopes) {
        foreach ($field in $scope.fields) {
            $fields += [ordered]@{
                name = $field.name
                title = $field.name
                scope = $scope.scope
                dataType = $field.adoType
                maxLength = $field.maxLength
                precision = $field.precision
                scale = $field.scale
                adoDbType = $field.adoDbType
                adoFixedLength = $field.fixedLength
                adoSchemaNullable = $field.schemaNullable
                adoMayBeNull = $field.mayBeNull
                adoKeyColumn = $field.keyColumn
                adoBaseTable = $field.baseTable
                adoBaseColumn = $field.baseColumn
                requiredMode = "unverified"
                requiredWhen = $null
                createWritable = $false
                updateWritable = $false
                clearable = $false
                valueSource = "unverified"
                u8Field = $field.name
                validation = $null
                returnable = $true
                evidence = @(
                    "$($evidence.collectionMode)",
                    "observedInSample=$($field.observedInSample)"
                )
            }
        }
    }

    $contract = [ordered]@{
        contractVersion = 1
        documentType = $evidence.documentType
        title = $titles[$evidence.documentType]
        status = "draft"
        u8 = [ordered]@{
            version = $sourceMap.u8Version
            component = $mapping.component
            componentDocumentType = [string]$mapping.componentDocumentType
            voucherType = $mapping.databaseVoucherType
            numberType = $null
            businessType = $mapping.businessType
            headerTable = $mapping.headerTable
            lineTable = $mapping.lineTable
            positionTable = $mapping.positionTable
        }
        capabilities = [ordered]@{
            query = [bool]$mapping.queryVerified
            validate = $false
            create = $false
            update = $false
            verify = $false
            unverify = $false
            delete = $false
        }
        fields = $fields
    }

    $outputPath = Join-Path $ContractsRoot (
        "$($evidence.documentType).json")
    $json = $contract | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText(
        $outputPath,
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
    Write-Output "$($evidence.documentType): $($fields.Count) fields"
}
