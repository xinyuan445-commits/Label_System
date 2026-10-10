param(
    [Parameter(Mandatory = $true)]
    [string]$JobsPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$SettingsPath = "D:\MaterialOutboundApi\App_Data\settings.json",

    [string]$Database = "UFDATA_008_2021"
)

$ErrorActionPreference = "Stop"

if ([Environment]::Is64BitProcess) {
    throw "This tool must run in 32-bit Windows PowerShell."
}

if ($Database -notmatch '^UFDATA_[0-9]{3}_[0-9]{4}$') {
    throw "The account database name is invalid."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Write-DiscoveryError {
    param([Exception]$Exception)

    [pscustomobject]@{
        succeeded = $false
        message = $Exception.Message
        exceptionType = $Exception.GetType().FullName
        occurredAt = (Get-Date).ToString("o")
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $OutputDirectory "error.json") -Encoding UTF8
}

trap {
    Write-DiscoveryError $_.Exception
    exit 1
}

function Unprotect-Text {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ""
    }

    Add-Type -AssemblyName System.Security
    $entropy = [Text.Encoding]::UTF8.GetBytes("MaterialOutboundApi.Settings.v1")
    $protectedValue = [Convert]::FromBase64String($Value)
    $plainValue = [Security.Cryptography.ProtectedData]::Unprotect(
        $protectedValue,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [Text.Encoding]::UTF8.GetString($plainValue)
}

$siteRoot = Split-Path -Parent (Split-Path -Parent $SettingsPath)
$interopDirectory = Join-Path $siteRoot "bin"
foreach ($assemblyName in @(
    "adodb.dll",
    "Interop.VBA.dll",
    "Interop.Scripting.dll",
    "Interop.USCOMMON.dll",
    "Interop.U8Login.dll",
    "Interop.MSXML2.dll",
    "Interop.USERPCO.dll")) {
    Add-Type -Path (Join-Path $interopDirectory $assemblyName)
}

$helperSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MSXML2;
using USERPCO;

public sealed class U8ReadOnlyVoucherJob
{
    public string DocumentCode { get; set; }
    public string VoucherType { get; set; }
    public int VoucherId { get; set; }
    public string VoucherCode { get; set; }
}

public sealed class U8ReadOnlyVoucherResult
{
    public string DocumentCode { get; set; }
    public string VoucherType { get; set; }
    public int VoucherId { get; set; }
    public string VoucherCode { get; set; }
    public bool Loaded { get; set; }
    public string Error { get; set; }
    public string Directory { get; set; }
}

public static class U8ReadOnlyVoucherExporter
{
    public static U8ReadOnlyVoucherResult[] Export(
        string subId,
        string account,
        string year,
        string user,
        string password,
        string loginDate,
        string server,
        string serial,
        U8ReadOnlyVoucherJob[] jobs,
        string outputDirectory)
    {
        U8Login.clsLogin login = null;
        VoucherCO voucher = null;
        var results = new List<U8ReadOnlyVoucherResult>();

        try
        {
            login = new U8Login.clsLogin();
            if (!login.Login(subId, account, year, user, password, loginDate, server, serial))
            {
                throw new InvalidOperationException("U8 login failed: " + login.ShareString);
            }

            voucher = new VoucherCOClass();
            var initializeError = string.Empty;
            if (!voucher.IniLogin(login, ref initializeError))
            {
                throw new InvalidOperationException("VoucherCO init failed: " + initializeError);
            }

            foreach (var job in jobs)
            {
                IXMLDOMDocument2 head = new DOMDocument26Class();
                IXMLDOMDocument2 body = new DOMDocument26Class();
                IXMLDOMDocument2 position = new DOMDocument26Class();
                var error = string.Empty;
                var result = new U8ReadOnlyVoucherResult
                {
                    DocumentCode = job.DocumentCode,
                    VoucherType = job.VoucherType,
                    VoucherId = job.VoucherId,
                    VoucherCode = job.VoucherCode,
                    Loaded = false
                };

                try
                {
                    result.Loaded = voucher.Load(
                        job.VoucherType,
                        "ID=" + job.VoucherId,
                        ref head,
                        ref body,
                        ref position,
                        ref error,
                        false,
                        string.Empty);
                    result.Error = error;

                    if (result.Loaded)
                    {
                        var safeDocumentCode = SafeSegment(job.DocumentCode);
                        var safeVoucherCode = SafeSegment(job.VoucherCode);
                        var jobDirectory = Path.Combine(
                            outputDirectory,
                            safeDocumentCode,
                            safeVoucherCode + "_" + job.VoucherId);
                        Directory.CreateDirectory(jobDirectory);
                        WriteXml(Path.Combine(jobDirectory, "head.xml"), head);
                        WriteXml(Path.Combine(jobDirectory, "body.xml"), body);
                        WriteXml(Path.Combine(jobDirectory, "position.xml"), position);
                        result.Directory = jobDirectory;
                    }
                }
                catch (Exception exception)
                {
                    result.Error = exception.Message;
                }
                finally
                {
                    Release(position);
                    Release(body);
                    Release(head);
                }

                results.Add(result);
            }

            return results.ToArray();
        }
        finally
        {
            if (login != null)
            {
                try { login.ShutDown(); } catch { }
            }
            Release(voucher);
            Release(login);
        }
    }

    private static void WriteXml(string path, IXMLDOMDocument2 document)
    {
        File.WriteAllText(
            path,
            document == null ? string.Empty : document.xml,
            new UTF8Encoding(false));
    }

    private static string SafeSegment(string value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }
        return result;
    }

    private static void Release(object value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
'@

Add-Type -TypeDefinition $helperSource -Language CSharp -ReferencedAssemblies @(
    (Join-Path $interopDirectory "Interop.U8Login.dll"),
    (Join-Path $interopDirectory "Interop.MSXML2.dll"),
    (Join-Path $interopDirectory "Interop.USERPCO.dll"))

$stored = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$u8Password = Unprotect-Text ([string]$stored.U8PasswordProtected)
if ([string]::IsNullOrWhiteSpace($u8Password)) {
    throw "No usable U8 password is available in the server-local settings."
}

$accountCode = $Database.Substring("UFDATA_".Length, 3)
$configuredAccount = [string]$stored.Public.U8AccId
$separator = $configuredAccount.LastIndexOf("@")
$u8Account = if ($separator -ge 0) {
    $configuredAccount.Substring(0, $separator) + "@" + $accountCode
} else {
    "(default)@" + $accountCode
}

$jobsInput = Get-Content -LiteralPath $JobsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$jobs = @($jobsInput) | ForEach-Object {
    $job = New-Object U8ReadOnlyVoucherJob
    $job.DocumentCode = [string]$_.documentCode
    $job.VoucherType = [string]$_.voucherType
    $job.VoucherId = [int]$_.voucherId
    $job.VoucherCode = [string]$_.voucherCode
    $job
}

if ($jobs.Count -eq 0) {
    throw "No voucher export jobs were supplied."
}

$results = [U8ReadOnlyVoucherExporter]::Export(
    [string]$stored.Public.U8SubId,
    $u8Account,
    [string]$stored.Public.U8Year,
    [string]$stored.Public.U8UserId,
    $u8Password,
    (Get-Date).ToString("yyyy-MM-dd"),
    [string]$stored.Public.U8Server,
    [string]$stored.Public.U8Serial,
    [U8ReadOnlyVoucherJob[]]$jobs,
    $OutputDirectory)

[pscustomobject]@{
    succeeded = (@($results | Where-Object { -not $_.Loaded }).Count -eq 0)
    database = $Database
    exportedAt = (Get-Date).ToString("o")
    readOnly = $true
    results = $results
} | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $OutputDirectory "result.json") -Encoding UTF8

$u8Password = $null
