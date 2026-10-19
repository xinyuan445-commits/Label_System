param(
    [string]$ProjectDir = (Join-Path $PSScriptRoot '..\label_system'),
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\releases\ct58c-test'),
    [string]$PackageName = 'com.tencent.weauth',
    [string]$BaseApkPath = ''
)

$ErrorActionPreference = 'Stop'
$workspaceDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectDir = [IO.Path]::GetFullPath($ProjectDir)
$outputDir = [IO.Path]::GetFullPath($OutputDir)
$miniProjectPath = Join-Path $projectDir 'project.miniapp.json'
$miniProject = Get-Content -Raw -Encoding UTF8 -LiteralPath $miniProjectPath | ConvertFrom-Json
$version = [string]$miniProject.version
$versionCode = [string]$miniProject.versionCode

$wechatCli = 'C:\Program Files (x86)\Tencent\微信web开发者工具\cli.bat'
$androidSdk = 'C:\Program Files (x86)\Android\android-sdk'
$buildTools = Join-Path $androidSdk 'build-tools\35.0.0'
$androidJar = Join-Path $androidSdk 'platforms\android-35\android.jar'
$jdkHome = 'C:\Program Files (x86)\Android\openjdk\jdk-17.0.14'
$env:JAVA_HOME = $jdkHome
$java = Join-Path $jdkHome 'bin\java.exe'
$javac = Join-Path $jdkHome 'bin\javac.exe'
$jar = Join-Path $jdkHome 'bin\jar.exe'
$d8 = Join-Path $buildTools 'd8.bat'
$zipalign = Join-Path $buildTools 'zipalign.exe'
$apksigner = Join-Path $buildTools 'apksigner.bat'
$apktool = Join-Path $workspaceDir '.tools\apktool_3.0.2.jar'
$keyStore = Join-Path $workspaceDir 'releases\test-signing\label-system-debug.keystore'
$credentialsPath = Join-Path $workspaceDir 'releases\test-signing\signing.local.json'
$bridgeSourceDir = Join-Path $workspaceDir 'android-bridge\src\com\xinyuan\labelbridge'
$bridgeAssetDir = Join-Path $workspaceDir 'android-bridge\assets'

$requiredPaths = @(
    $wechatCli,
    $androidJar,
    $java,
    $javac,
    $jar,
    $d8,
    $zipalign,
    $apksigner,
    $apktool,
    $keyStore,
    $credentialsPath,
    (Join-Path $bridgeSourceDir 'PdaBridgeServer.java'),
    (Join-Path $bridgeSourceDir 'Ct58cScanReceiver.java'),
    (Join-Path $bridgeSourceDir 'Ct58cBridgeInitializer.java'),
    (Join-Path $bridgeAssetDir 'scan_success_zh_female.wav'),
    (Join-Path $bridgeAssetDir 'scan_error_zh_female.wav'),
    (Join-Path $bridgeAssetDir 'startup-overlay.html')
)
foreach ($path in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Build dependency not found: $path"
    }
}

$credentials = Get-Content -Raw -Encoding UTF8 -LiteralPath $credentialsPath | ConvertFrom-Json
$storePassword = [string]$credentials.storePassword
$keyPassword = [string]$credentials.keyPassword
$keyAlias = [string]$credentials.keyAlias
if (-not $storePassword -or -not $keyPassword -or -not $keyAlias) {
    throw 'Local signing credentials are incomplete.'
}

$buildId = [guid]::NewGuid().ToString('N')
$workRoot = Join-Path $workspaceDir "releases\build-work\$buildId"
$baseDir = Join-Path $workRoot 'base'
$decodedDir = Join-Path $workRoot 'decoded'
$classesDir = Join-Path $workRoot 'classes'
$dexDir = Join-Path $workRoot 'dex'
$injectDir = Join-Path $workRoot 'inject'
$unsignedApk = Join-Path $workRoot 'unsigned.apk'
$rebuiltApk = Join-Path $workRoot 'rebuilt.apk'
$alignedApk = Join-Path $workRoot 'aligned.apk'
$signedApk = Join-Path $workRoot 'signed.apk'
$finalApk = Join-Path $outputDir "label-system-$version-ct58c.apk"

New-Item -ItemType Directory -Force -Path `
    $baseDir, $classesDir, $dexDir, $injectDir, $outputDir | Out-Null

if ($BaseApkPath) {
    $resolvedBaseApkPath = [IO.Path]::GetFullPath($BaseApkPath)
    if (-not (Test-Path -LiteralPath $resolvedBaseApkPath -PathType Leaf)) {
        throw "Base APK not found: $resolvedBaseApkPath"
    }
    $baseApk = Get-Item -LiteralPath $resolvedBaseApkPath
} else {
    & $wechatCli build-apk `
        --project $projectDir `
        --keyPass $keyPassword `
        --storePass $storePassword `
        --keyAlias $keyAlias `
        --keyStore $keyStore `
        --useAab false `
        --desc 'CT58C native bridge test' `
        --output $baseDir `
        --lang zh

    $baseApk = Get-ChildItem -LiteralPath $baseDir -File -Filter '*.apk' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}
if (-not $baseApk) {
    throw 'WeChat DevTools did not produce a base APK.'
}

if ($BaseApkPath) {
    # 复用固定包名的既有壳包时，只解码资源，避免重编译整个第三方运行时 DEX。
    & $java -jar $apktool d -s $baseApk.FullName -o $decodedDir
} else {
    & $java -jar $apktool d $baseApk.FullName -o $decodedDir
}
if ($LASTEXITCODE -ne 0) { throw 'APK decode failed.' }

$manifestPath = Join-Path $decodedDir 'AndroidManifest.xml'
[xml]$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath
$generatedPackageName = $manifest.DocumentElement.GetAttribute('package')
if (-not $generatedPackageName) { throw 'APK manifest has no package name.' }

# 微信后台可能调整生成包名。发布壳必须保持固定 applicationId，才能覆盖升级并保留本地配置。
if ($generatedPackageName -ne $PackageName) {
    if ($BaseApkPath) { throw 'Base APK must already use the fixed package name for resource-only rebuilds.' }
    $generatedPackagePath = $generatedPackageName.Replace('.', '/')
    $packagePath = $PackageName.Replace('.', '/')
    $rewriteFiles = @($manifestPath) + @(
        Get-ChildItem -LiteralPath $decodedDir -Directory -Filter 'smali*' |
            ForEach-Object {
                Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Filter '*.smali'
            } |
            Select-Object -ExpandProperty FullName
    )
    $utf8NoBom = New-Object Text.UTF8Encoding($false)
    foreach ($rewriteFile in $rewriteFiles) {
        $content = [IO.File]::ReadAllText($rewriteFile)
        if ($content.Contains($generatedPackageName) -or $content.Contains($generatedPackagePath)) {
            $content = $content.Replace($generatedPackageName, $PackageName)
            $content = $content.Replace($generatedPackagePath, $packagePath)
            [IO.File]::WriteAllText($rewriteFile, $content, $utf8NoBom)
        }
    }
    [xml]$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath
}

$androidNamespace = 'http://schemas.android.com/apk/res/android'
$manifest.DocumentElement.SetAttribute('versionName', $androidNamespace, $version)
$manifest.DocumentElement.SetAttribute('versionCode', $androidNamespace, $versionCode)
$application = $manifest.SelectSingleNode('/manifest/application')
if (-not $application) { throw 'APK manifest has no application element.' }
$application.SetAttribute('usesCleartextTraffic', $androidNamespace, 'true')
$application.SetAttribute('icon', $androidNamespace, '@mipmap/ic_launcher')
$application.SetAttribute('roundIcon', $androidNamespace, '@mipmap/ic_launcher')

# 覆盖升级包也可作基包，先去掉旧桥注册，避免重复 provider 和扫码接收器。
foreach ($node in @($application.ChildNodes)) {
    if ($node.LocalName -in @('receiver','provider') -and
        $node.GetAttribute('name', $androidNamespace) -in @(
            'com.xinyuan.labelbridge.Ct58cScanReceiver',
            'com.xinyuan.labelbridge.Ct58cBridgeInitializer')) {
        [void]$application.RemoveChild($node)
    }
}

$receiver = $manifest.CreateElement('receiver')
$receiver.SetAttribute('name', $androidNamespace, 'com.xinyuan.labelbridge.Ct58cScanReceiver')
$receiver.SetAttribute('enabled', $androidNamespace, 'true')
$receiver.SetAttribute('exported', $androidNamespace, 'true')
$intentFilter = $manifest.CreateElement('intent-filter')
foreach ($actionName in @('android.intent.ACTION_DECODE_DATA', 'android.intent.action.SCANRESULT')) {
    $action = $manifest.CreateElement('action')
    $action.SetAttribute('name', $androidNamespace, $actionName)
    [void]$intentFilter.AppendChild($action)
}
[void]$receiver.AppendChild($intentFilter)
[void]$application.AppendChild($receiver)

$provider = $manifest.CreateElement('provider')
$provider.SetAttribute('name', $androidNamespace, 'com.xinyuan.labelbridge.Ct58cBridgeInitializer')
$provider.SetAttribute('authorities', $androidNamespace, "$PackageName.label-pda-bridge")
$provider.SetAttribute('enabled', $androidNamespace, 'true')
$provider.SetAttribute('exported', $androidNamespace, 'false')
$provider.SetAttribute('initOrder', $androidNamespace, '100')
[void]$application.AppendChild($provider)

$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($manifestPath, $manifest.OuterXml, $utf8NoBom)

$apktoolConfig = Join-Path $decodedDir 'apktool.yml'
$yaml = [IO.File]::ReadAllText($apktoolConfig)
$yaml = [regex]::Replace($yaml, '(?m)^  versionCode:.*$', "  versionCode: $versionCode")
$yaml = [regex]::Replace($yaml, '(?m)^  versionName:.*$', "  versionName: $version")
[IO.File]::WriteAllText($apktoolConfig, $yaml, $utf8NoBom)
& (Join-Path $PSScriptRoot 'apply_apk_branding.ps1') -DecodedDir $decodedDir

& $java -jar $apktool b --jobs 2 $decodedDir -o $rebuiltApk
if ($LASTEXITCODE -ne 0) { throw 'APK rebuild failed.' }

$bridgeSources = Get-ChildItem -LiteralPath $bridgeSourceDir -File -Filter '*.java' |
    Select-Object -ExpandProperty FullName
& $javac -encoding UTF-8 -source 8 -target 8 -classpath $androidJar -d $classesDir $bridgeSources
if ($LASTEXITCODE -ne 0) { throw 'Native bridge Java compilation failed.' }

$classFiles = Get-ChildItem -LiteralPath $classesDir -Recurse -File -Filter '*.class' |
    Select-Object -ExpandProperty FullName
& $d8 --lib $androidJar --min-api 24 --output $dexDir $classFiles
if ($LASTEXITCODE -ne 0) { throw 'Native bridge DEX compilation failed.' }

Copy-Item -Force -LiteralPath (Join-Path $dexDir 'classes.dex') -Destination (Join-Path $injectDir 'classes3.dex')
$injectAssetDir = Join-Path $injectDir 'assets'
New-Item -ItemType Directory -Force -Path $injectAssetDir | Out-Null
Copy-Item -Force -LiteralPath (Join-Path $bridgeAssetDir 'scan_success_zh_female.wav') -Destination $injectAssetDir
Copy-Item -Force -LiteralPath (Join-Path $bridgeAssetDir 'scan_error_zh_female.wav') -Destination $injectAssetDir
Copy-Item -Force -LiteralPath (Join-Path $bridgeAssetDir 'startup-overlay.html') -Destination $injectAssetDir
Copy-Item -Force -LiteralPath $rebuiltApk -Destination $unsignedApk
Push-Location $injectDir
try {
    & $jar uf $unsignedApk 'classes3.dex' 'assets'
    if ($LASTEXITCODE -ne 0) { throw 'Native bridge DEX injection failed.' }
} finally {
    Pop-Location
}

& $zipalign -f 4 $unsignedApk $alignedApk
if ($LASTEXITCODE -ne 0) { throw 'APK alignment failed.' }

& $apksigner sign `
    --ks $keyStore `
    --ks-key-alias $keyAlias `
    --ks-pass "pass:$storePassword" `
    --key-pass "pass:$keyPassword" `
    --out $signedApk `
    $alignedApk
if ($LASTEXITCODE -ne 0) { throw 'APK signing failed.' }

& $apksigner verify --verbose $signedApk
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }

$entries = & $jar tf $signedApk
if ($entries -notcontains 'classes3.dex') { throw 'Final APK is missing classes3.dex.' }
if ($entries -notcontains 'assets/scan_success_zh_female.wav') { throw 'Final APK is missing scan_success_zh_female.wav.' }
if ($entries -notcontains 'assets/scan_error_zh_female.wav') { throw 'Final APK is missing scan_error_zh_female.wav.' }
& (Join-Path $PSScriptRoot 'verify_apk_branding.ps1') -ApkPath $signedApk -ExpectedVersion $version -ExpectedVersionCode $versionCode
Copy-Item -Force -LiteralPath $signedApk -Destination $finalApk

$file = Get-Item -LiteralPath $finalApk
$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $finalApk
Write-Output "APK=$($file.FullName)"
Write-Output "SIZE=$($file.Length)"
Write-Output "SHA256=$($hash.Hash)"
