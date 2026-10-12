[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$apiRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $apiRoot 'src\LabelSystem.Api\LabelSystem.Api.csproj'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $apiRoot 'runtime-data\publish\LabelSystem.Api'
}
$publishPath = [IO.Path]::GetFullPath($OutputPath)

dotnet restore $projectPath
if ($LASTEXITCODE -ne 0) { throw 'Business API 依赖还原失败。' }
dotnet publish $projectPath -c Release --no-restore -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'Business API 发布构建失败。' }

$requiredFiles = @(
    'LabelSystem.Api.dll',
    'LabelSystem.Api.runtimeconfig.json',
    'web.config'
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $file))) {
        throw "发布包缺少文件：$file"
    }
}

[pscustomobject]@{
    PublishPath = $publishPath
    Version = [Diagnostics.FileVersionInfo]::GetVersionInfo(
        (Join-Path $publishPath 'LabelSystem.Api.dll')).ProductVersion
}
