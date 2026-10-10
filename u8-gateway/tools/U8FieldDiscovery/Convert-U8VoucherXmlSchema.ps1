param(
    [Parameter(Mandatory = $true)]
    [string]$InputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$CollectionMode = "USERPCO.VoucherCO.Load read-only"
)

$ErrorActionPreference = "Stop"

function Get-AttributeByLocalName {
    param(
        [System.Xml.XmlElement]$Element,
        [string]$Name
    )

    if ($null -eq $Element) {
        return $null
    }

    foreach ($attribute in $Element.Attributes) {
        if ($attribute.LocalName -eq $Name) {
            return $attribute.Value
        }
    }
    return $null
}

function Convert-OptionalBoolean {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }
    return [bool]::Parse($Value)
}

function Convert-OptionalInteger {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }
    return [int]$Value
}

$inputRoot = (Resolve-Path -LiteralPath $InputDirectory).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$documentDirectories = Get-ChildItem -LiteralPath $inputRoot -Directory
foreach ($documentDirectory in $documentDirectories) {
    $scopes = @()
    $sampleDirectory = Get-ChildItem -LiteralPath $documentDirectory.FullName -Directory |
        Select-Object -First 1
    if ($null -eq $sampleDirectory) {
        continue
    }

    foreach ($scopeName in @("head", "body", "position")) {
        $xmlPath = Join-Path $sampleDirectory.FullName ($scopeName + ".xml")
        if (-not (Test-Path -LiteralPath $xmlPath)) {
            continue
        }
        if ((Get-Item -LiteralPath $xmlPath).Length -eq 0) {
            $scopes += [ordered]@{
                scope = if ($scopeName -eq "head") { "header" } elseif ($scopeName -eq "body") { "line" } else { "position" }
                recordCount = 0
                fields = @()
            }
            continue
        }

        $document = New-Object System.Xml.XmlDocument
        $document.Load($xmlPath)
        $rowNodes = @($document.SelectNodes("//*[local-name()='row']"))
        $fieldNodes = @($document.SelectNodes("//*[local-name()='AttributeType']"))
        $fields = foreach ($fieldNode in $fieldNodes) {
            $dataTypeNode = $fieldNode.SelectSingleNode("./*[local-name()='datatype']")
            $name = Get-AttributeByLocalName $fieldNode "name"
            $observed = $false
            foreach ($rowNode in $rowNodes) {
                if ($null -ne $rowNode.Attributes.GetNamedItem($name)) {
                    $observed = $true
                    break
                }
            }

            [ordered]@{
                order = Convert-OptionalInteger (Get-AttributeByLocalName $fieldNode "number")
                name = $name
                adoType = Get-AttributeByLocalName $dataTypeNode "type"
                maxLength = Convert-OptionalInteger (Get-AttributeByLocalName $dataTypeNode "maxLength")
                precision = Convert-OptionalInteger (Get-AttributeByLocalName $dataTypeNode "precision")
                scale = Convert-OptionalInteger (Get-AttributeByLocalName $dataTypeNode "scale")
                adoDbType = Get-AttributeByLocalName $dataTypeNode "dbtype"
                fixedLength = Convert-OptionalBoolean (Get-AttributeByLocalName $dataTypeNode "fixedlength")
                schemaNullable = Convert-OptionalBoolean (Get-AttributeByLocalName $fieldNode "nullable")
                mayBeNull = Convert-OptionalBoolean (Get-AttributeByLocalName $dataTypeNode "maybenull")
                keyColumn = Convert-OptionalBoolean (Get-AttributeByLocalName $fieldNode "keycolumn")
                schemaWriteUnknownHint = Convert-OptionalBoolean (Get-AttributeByLocalName $fieldNode "writeunknown")
                baseTable = Get-AttributeByLocalName $fieldNode "basetable"
                baseColumn = Get-AttributeByLocalName $fieldNode "basecolumn"
                observedInSample = $observed
            }
        }

        $scopes += [ordered]@{
            scope = if ($scopeName -eq "head") { "header" } elseif ($scopeName -eq "body") { "line" } else { "position" }
            recordCount = $rowNodes.Count
            fields = @($fields)
        }
    }

    $evidence = [ordered]@{
        evidenceVersion = 1
        documentType = $documentDirectory.Name
        u8Version = "16.0"
        collectedAt = "2026-10-10"
        collectionMode = $CollectionMode
        environment = "production account read-only sample"
        containsBusinessValues = $false
        requirednessVerified = $false
        writableRulesVerified = $false
        notes = @(
            "ADO nullable metadata does not prove API requiredness.",
            "ADO writeunknown metadata is only a hint and does not prove create/update support."
        )
        scopes = $scopes
    }

    $outputPath = Join-Path $OutputDirectory ($documentDirectory.Name + ".json")
    $evidence | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $outputPath -Encoding UTF8
}
