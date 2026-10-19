param([Parameter(Mandatory)][string]$DecodedDir)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$decoded = [IO.Path]::GetFullPath($DecodedDir)
$res = Join-Path $decoded 'res'
$brand = Join-Path $workspace 'android-bridge\branding'
$nativeResources = Join-Path $workspace 'label_system\miniapp\android\nativeResources\res'
if (-not (Test-Path -LiteralPath (Join-Path $decoded 'AndroidManifest.xml'))) {
    throw 'Branding target is not a decoded APK.'
}
foreach ($density in @('mdpi','hdpi','xhdpi','xxhdpi','xxxhdpi')) {
    $source = Join-Path $nativeResources "mipmap-$density\ic_launcher.png"
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing branding asset: $source" }
    $target = Join-Path $res "mipmap-$density"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $target 'ic_launcher.png') -Force
}

# 保留框架需要的 ID、视图与加载流程，只覆盖壳品牌资源。
Copy-Item -LiteralPath (Join-Path $brand 'startup.xml') -Destination (Join-Path $res 'layout\layout_splashscreen.xml') -Force
Copy-Item -LiteralPath (Join-Path $brand 'startup.xml') -Destination (Join-Path $res 'layout\layout.xml') -Force
$utf8 = [Text.UTF8Encoding]::new($false)
$count = 0
foreach ($file in (Get-ChildItem -LiteralPath $res -Directory -Filter 'values*' | ForEach-Object {
    Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.xml'
})) {
    [xml]$xml = [IO.File]::ReadAllText($file.FullName)
    $nodes = @($xml.SelectNodes('/resources/string[@name="wxa_brand_hint_donut"]'))
    if ($nodes.Count -gt 0) {
        foreach ($node in $nodes) { $node.InnerText = ''; $count++ }
        [IO.File]::WriteAllText($file.FullName, $xml.OuterXml, $utf8)
    }
}
if ($count -eq 0) { throw 'Expected Donut startup branding resource was not found; review the new base APK.' }

foreach ($file in (Get-ChildItem -LiteralPath $res -Recurse -File -Filter 'wmpf_splash_wxa_brand_icon.png')) {
    Copy-Item -LiteralPath (Join-Path $brand 'transparent.png') -Destination $file.FullName -Force
}
$loadingLayout = Join-Path $res 'layout\wmpf_splash_ui.xml'
if (Test-Path -LiteralPath $loadingLayout) {
    [xml]$xml = [IO.File]::ReadAllText($loadingLayout)
    $androidNs = 'http://schemas.android.com/apk/res/android'
    foreach ($node in $xml.SelectNodes('//*')) {
        if ($node.GetAttribute('id', $androidNs) -eq '@id/wmpf_splash_icon') {
            $node.SetAttribute('visibility', $androidNs, 'gone')
            $node.SetAttribute('layout_width', $androidNs, '0dp')
            $node.SetAttribute('layout_height', $androidNs, '0dp')
        }
    }
    # Keep SDK progress/avatar/name objects and IDs for its code; place our decoration above them.
    foreach ($node in @($xml.SelectNodes('//*'))) {
        if ($node.GetAttribute('tag', $androidNs) -eq 'label_startup_brand_overlay') {
            [void]$node.ParentNode.RemoveChild($node)
        }
    }
    [xml]$startup = [IO.File]::ReadAllText((Join-Path $brand 'startup.xml'))
    $overlay = $xml.CreateElement('FrameLayout')
    $overlay.SetAttribute('tag', $androidNs, 'label_startup_brand_overlay')
    $overlay.SetAttribute('layout_width', $androidNs, 'match_parent')
    $overlay.SetAttribute('layout_height', $androidNs, 'match_parent')
    $overlay.SetAttribute('background', $androidNs, '#F3F6FA')
    $content = $xml.ImportNode($startup.DocumentElement.FirstChild, $true)
    $content.SetAttribute('layout_gravity', $androidNs, 'center')
    foreach ($icon in $content.SelectNodes('.//com.xinyuan.labelbridge.StartupBrandView')) {
        $icon.SetAttribute('tag', $androidNs, 'label_startup_static')
    }
    [void]$overlay.AppendChild($content)
    # Keep the SDK's optional compliance hint above the decorative layer.
    $hint = $xml.SelectNodes('//*') | Where-Object { $_.GetAttribute('id', $androidNs) -eq '@id/wmpf_splash_game_hint' } | Select-Object -First 1
    if ($hint) { [void]$xml.DocumentElement.InsertBefore($overlay, $hint) }
    else { [void]$xml.DocumentElement.AppendChild($overlay) }
    [IO.File]::WriteAllText($loadingLayout, $xml.OuterXml, $utf8)
}
Write-Output "Branding applied; cleared $count startup text resources."
