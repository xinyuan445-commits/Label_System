param([string]$BaseUrl='http://172.16.1.159:9891',[Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
if($BaseUrl -cne 'http://172.16.1.159:9891'){throw 'Development URL boundary rejected'}
$health=Invoke-RestMethod -Uri ($BaseUrl+'/api/v1/health')
if($health.data.environment -ne 'development' -or $health.data.u8WritesEnabled){throw 'Development write gate failed'}
New-Item -ItemType Directory -Path $EvidenceDirectory -Force|Out-Null
$login=Invoke-RestMethod -Uri ($BaseUrl+'/api/v1/auth/login') -Method Post -ContentType 'application/json' `
    -Body (@{username='admin';password='admin'}|ConvertTo-Json) -SessionVariable session
$null=$login
$cookieNames=@($session.Cookies.GetCookies([Uri]$BaseUrl)|ForEach-Object {$_.Name})
if($cookieNames -notcontains 'LabelSystem.DevPrint.Session' -or $cookieNames -contains 'LabelSystem.Session'){throw 'Development session isolation failed'}
function Get-Dev([string]$path){Invoke-RestMethod -Uri ($BaseUrl+$path) -WebSession $session}
function Post-Dev([string]$path,$body){Invoke-RestMethod -Uri ($BaseUrl+$path) -WebSession $session -Method Post -ContentType 'application/json; charset=utf-8' -Body ($body|ConvertTo-Json -Depth 16)}
$cap=Get-Dev '/api/v1/print/capabilities'
if(-not $cap.data.enabled -or $cap.data.allowNewDispatch){throw 'Print dispatch gate failed'}
$pending=Get-Dev '/api/v1/print/pending-labels';$jobs=Get-Dev '/api/v1/print/jobs'
$templates=Get-Dev '/api/v1/print/templates'
$name='开发预览模板 76.2×50.8'
$template=@($templates.data|Where-Object {$_.name -eq $name}|Select-Object -First 1)
if($template.Count -eq 0){
    $canvas=@{version=1;stage=@{width_mm=76.2;height_mm=50.8};objects=@(
        @{id='code';type='barcode';barcodeType='qr';data='{{Code}}';x=2;y=2;width=25;height=25},
        @{id='material';type='text';text='物料：{{物料编码}}';x=30;y=3;width=43;height=10;fontSize=3},
        @{id='qty';type='text';text='数量：{{数量}}';x=30;y=14;width=43;height=10;fontSize=3})}
    $saved=Post-Dev '/api/v1/print/templates' @{name=$name;canvasData=$canvas;widthMm=76.2;heightMm=50.8}
    $published=Post-Dev ('/api/v1/print/templates/'+$saved.data.id+'/publish') @{expectedRowVersion=$saved.data.row_version}
    $version=$published.data.id;$templateId=$saved.data.id
}else{$templateId=$template[0].id;$version=$template[0].publishedVersionId}
$previewFile=Join-Path $EvidenceDirectory 'development-preview.pdf'
$preview=@{templateVersionId=$version;variables=@{'物料编码'='PREVIEW-MATERIAL';'数量'='12.5'}}|ConvertTo-Json -Depth 8
Invoke-WebRequest -Uri ($BaseUrl+'/api/v1/print/previews') -Method Post -WebSession $session -ContentType 'application/json; charset=utf-8' -Body $preview -OutFile $previewFile
$bytes=[IO.File]::ReadAllBytes($previewFile)
if([Text.Encoding]::ASCII.GetString($bytes,0,5) -ne '%PDF-'){throw 'Preview is not a PDF'}
$firstBlocked=$false
try{Post-Dev '/api/v1/print/jobs/first' @{clientRequestId=[Guid]::NewGuid().ToString();templateVersionId=$version;deviceId=[Guid]::NewGuid().ToString();printer='No real printer';targets=@()}|Out-Null}
catch{if([int]$_.Exception.Response.StatusCode -eq 409){$firstBlocked=$true}else{throw}}
if(-not $firstBlocked){throw 'First print was not blocked'}
$reprintBlocked=$false
try{Post-Dev '/api/v1/print/jobs/reprint' @{clientRequestId=[Guid]::NewGuid().ToString();labelId=0;expectedRowVersion='0000000000000000';copies=1;reason='Gate check'}|Out-Null}
catch{if([int]$_.Exception.Response.StatusCode -eq 409){$reprintBlocked=$true}else{throw}}
if(-not $reprintBlocked){throw 'Reprint was not blocked'}
$root=Invoke-WebRequest -Uri ($BaseUrl+'/');$workspace=Invoke-WebRequest -Uri ($BaseUrl+'/printing/');$runtime=Invoke-RestMethod -Uri ($BaseUrl+'/runtime-config.json')
if($runtime.apiBaseUrl -ne '' -or $runtime.environment -ne 'development'){throw 'Frontend is not same-origin development'}
$result=[ordered]@{at=(Get-Date).ToString('o');baseUrl=$BaseUrl;health=$health.data;sessionCookieNames=$cookieNames;
    rootStatus=[int]$root.StatusCode;workspaceStatus=[int]$workspace.StatusCode;printEnabled=$cap.data.enabled;allowNewDispatch=$cap.data.allowNewDispatch;
    pendingCount=@($pending.data).Count;jobCount=@($jobs.data).Count;templateId=$templateId;templateVersion=$version;previewBytes=$bytes.Length;
    previewSha256=(Get-FileHash -LiteralPath $previewFile -Algorithm SHA256).Hash;firstPrintBlocked=$firstBlocked;reprintBlocked=$reprintBlocked;realPrinterStarted=$false}
$result|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'smoke-result.json') -Encoding utf8
$result|ConvertTo-Json -Depth 7
