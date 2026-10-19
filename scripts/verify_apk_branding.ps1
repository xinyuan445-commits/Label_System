param(
    [Parameter(Mandatory)][string]$ApkPath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][int]$ExpectedVersionCode
)
$ErrorActionPreference='Stop'
$aapt='C:\Program Files (x86)\Android\android-sdk\build-tools\35.0.0\aapt.exe'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$apk=[IO.Path]::GetFullPath($ApkPath)
$badging=& $aapt dump badging $apk
if($LASTEXITCODE -ne 0){throw 'Cannot read APK metadata.'}
if($badging[0] -notmatch "versionCode='$ExpectedVersionCode'" -or
   $badging[0] -notmatch ("versionName='"+[regex]::Escape($ExpectedVersion)+"'")){
    throw 'APK version does not match the project.'
}
$manifest=(& $aapt dump xmltree $apk AndroidManifest.xml) -join "`n"
foreach($class in @('Ct58cScanReceiver','Ct58cBridgeInitializer')){
    if([regex]::Matches($manifest,'android:name[^\r\n]*="com\.xinyuan\.labelbridge\.'+$class+'"').Count -ne 1){
        throw "Expected exactly one bridge registration: $class"
    }
}
$resources=& $aapt dump --values resources $apk
if($LASTEXITCODE -ne 0){throw 'Cannot read APK resources.'}
if($resources -match '(?i)Powered\s+By\s+Donut'){throw 'Old startup branding is still present.'}
$donut=@($resources|Select-String -Pattern 'resource 0x\w+ \S+:string/wxa_brand_hint_donut: t=' -Context 0,1)
if($donut.Count -eq 0){throw 'Startup branding resource was not verified.'}
foreach($entry in $donut){if(($entry.Context.PostContext -join '') -notmatch '\(string8\) ""'){
    throw 'Startup branding text was not cleared.'
}}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing
$zip=[IO.Compression.ZipFile]::OpenRead($apk)
try{
    foreach($density in @('mdpi','hdpi','xhdpi','xxhdpi','xxxhdpi')){
        $entry=$zip.GetEntry("res/mipmap-$density/ic_launcher.png")
        if(-not $entry){throw "Missing launcher density: $density"}
        $stream=$entry.Open()
        $actual=$null;$expected=$null
        try{
            # Android resource compilation may optimize PNG encoding; compare pixels, not file bytes.
            $actual=[Drawing.Bitmap]::new($stream)
            $expected=[Drawing.Bitmap]::new((Join-Path $workspace "label_system/miniapp/android/nativeResources/res/mipmap-$density/ic_launcher.png"))
            if($actual.Width -ne $expected.Width -or $actual.Height -ne $expected.Height){
                throw "Launcher dimensions differ from source: $density"
            }
            for($y=0;$y -lt $actual.Height;$y++){
                for($x=0;$x -lt $actual.Width;$x++){
                    $a=$actual.GetPixel($x,$y);$e=$expected.GetPixel($x,$y)
                    if($a.A -ne $e.A -or ($a.A -ne 0 -and $a.ToArgb() -ne $e.ToArgb())){
                        throw "Launcher pixels differ from source: $density ($x,$y)"
                    }
                }
            }
        }finally{
            if($actual){$actual.Dispose()};if($expected){$expected.Dispose()};$stream.Dispose()
        }
    }
    foreach($file in @('classes3.dex','assets/scan_success_zh_female.wav','assets/scan_error_zh_female.wav')){
        if(-not $zip.GetEntry($file)){throw "Missing native bridge resource: $file"}
    }
}finally{$zip.Dispose()}
Write-Output 'APK branding, version, bridge registration and launcher pixels verified.'
