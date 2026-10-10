[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$gatewayRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $gatewayRoot 'src\U8Gateway.Api\U8Gateway.Api.csproj'
$projectRoot = Split-Path $projectPath -Parent
$buildOutput = Join-Path $projectRoot "bin\$Configuration\net48"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $gatewayRoot 'runtime-data\publish\U8Gateway.Api'
}
$packageRoot = [IO.Path]::GetFullPath($OutputPath)

if ($packageRoot.Length -lt 12 -or $packageRoot -eq [IO.Path]::GetPathRoot($packageRoot)) {
    throw "拒绝使用不安全的发布目录：$packageRoot"
}

dotnet build $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'U8 Gateway 编译失败。' }

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
$binRoot = Join-Path $packageRoot 'bin'
$adminRoot = Join-Path $packageRoot 'Admin'
New-Item -ItemType Directory -Path $binRoot -Force | Out-Null
New-Item -ItemType Directory -Path $adminRoot -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $projectRoot 'Global.asax') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'Web.config') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'Admin\index.html') -Destination $adminRoot
Get-ChildItem -LiteralPath $buildOutput -File | Where-Object {
    $_.Extension -in '.dll', '.config'
} | Copy-Item -Destination $binRoot

$required = @(
    'Global.asax',
    'Web.config',
    'Admin\index.html',
    'bin\U8Gateway.Api.dll',
    'bin\U8Gateway.U8.dll',
    'bin\ADODB.dll'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $relative))) {
        throw "发布包缺少文件：$relative"
    }
}

[pscustomobject]@{
    PackagePath = $packageRoot
    FileCount = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File).Count
    Configuration = $Configuration
}
