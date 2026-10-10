param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("purchase", "sales")]
    [string]$Component,

    [Parameter(Mandatory = $true)]
    [string]$JobsPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$SettingsPath = "D:\MaterialOutboundApi\App_Data\settings.json",

    [string]$ComponentInteropDirectory = "D:\U8GatewayFieldDiscovery\components",

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
foreach ($staleFileName in @("error.json", "result.json", "progress.json")) {
    $stalePath = Join-Path $OutputDirectory $staleFileName
    if ([IO.File]::Exists($stalePath)) {
        [IO.File]::Delete($stalePath)
    }
}

function Write-DiscoveryStage {
    param([string]$Stage)

    [pscustomobject]@{
        stage = $Stage
        occurredAt = (Get-Date).ToString("o")
    } | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath (Join-Path $OutputDirectory "progress.json") -Encoding UTF8
}

Write-DiscoveryStage "script-started"

function Write-DiscoveryError {
    param([Exception]$Exception)

    [pscustomobject]@{
        succeeded = $false
        component = $Component
        message = $Exception.Message
        exceptionType = $Exception.GetType().FullName
        detail = $Exception.ToString()
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

$helperSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ADODB;
using Info_PU;
using MSXML2;
using SysInfo;
using U8Login;
using VoucherCO_PU;
using VoucherCO_SA;
using VoucherVerify;

public sealed class U8SpecializedReadJob
{
    public string DocumentCode { get; set; }
    public int VoucherId { get; set; }
    public string VoucherCode { get; set; }
    public string BusinessType { get; set; }
}

public sealed class U8SpecializedReadResult
{
    public string DocumentCode { get; set; }
    public int VoucherId { get; set; }
    public string VoucherCode { get; set; }
    public bool Loaded { get; set; }
    public string Error { get; set; }
    public string Directory { get; set; }
}

public static class U8SpecializedReadOnlyExporter
{
    public static U8SpecializedReadResult[] Export(
        string component,
        string subId,
        string account,
        string year,
        string user,
        string password,
        string loginDate,
        string server,
        string serial,
        U8SpecializedReadJob[] jobs,
        string outputDirectory)
    {
        clsLogin login = null;
        dynamic commonLogin = null;
        Connection connection = null;
        object voucher = null;
        var results = new List<U8SpecializedReadResult>();

        try
        {
            login = new clsLogin();
            Stage(outputDirectory, "login-object-created");
            if (!login.Login(subId, account, year, user, password, loginDate, server, serial))
            {
                throw new InvalidOperationException("U8 login failed: " + Convert.ToString(login.ShareString));
            }
            Stage(outputDirectory, "u8-login-complete");

            commonLogin = Create("USCOMMON.Login");
            clsLogin loginSource = login;
            commonLogin.Load(ref loginSource);
            Stage(outputDirectory, "common-login-complete");
            object accountConnection = commonLogin.AccountConnection;
            connection = ConvertConnection(accountConnection);
            Stage(outputDirectory, "connection-converted");

            voucher = component == "purchase"
                ? (object)new clsVoucherCO_PUClass()
                : Create("USSAServer.clsVouchLoad");
            Stage(outputDirectory, "voucher-object-created");

            Initialize(component, voucher, login, connection, commonLogin, jobs);
            Stage(outputDirectory, "voucher-initialized");

            foreach (var job in jobs)
            {
                IXMLDOMDocument2 head = null;
                IXMLDOMDocument2 body = null;
                var result = new U8SpecializedReadResult
                {
                    DocumentCode = job.DocumentCode,
                    VoucherId = job.VoucherId,
                    VoucherCode = job.VoucherCode,
                    Loaded = false
                };

                try
                {
                    head = new DOMDocument26Class();
                    body = new DOMDocument26Class();

                    if (component == "purchase")
                    {
                        var purchaseVoucher = (clsVoucherCO_PU)voucher;
                        string locateWhere = string.Empty;
                        object voucherId = job.VoucherId;
                        string where = string.Empty;
                        object response = purchaseVoucher.GetVoucherDataById(
                            ref head,
                            ref body,
                            where,
                            voucherId,
                            ref locateWhere);
                        result.Loaded = XmlHasContent(head) || XmlHasContent(body);
                        if (!result.Loaded)
                        {
                            result.Error = "The purchase component returned no XML.";
                        }
                    }
                    else
                    {
                        dynamic salesVoucher = voucher;
                        object voucherId = job.VoucherId;
                        bool authorize = true;
                        string where = "DLID=" + job.VoucherId;
                        short showStyle = 0;
                        string error = string.Empty;
                        object salesHead = head;
                        object salesBody = body;
                        bool response = salesVoucher.GetVoucherDataWEB(
                            ref salesHead,
                            ref salesBody,
                            connection,
                            5,
                            false,
                            ref error,
                            voucherId,
                            ref authorize,
                            ref where,
                            ref showStyle);
                        head = salesHead as IXMLDOMDocument2;
                        body = salesBody as IXMLDOMDocument2;
                        result.Loaded = XmlHasContent(head) || XmlHasContent(body);
                        if (!result.Loaded)
                        {
                            result.Error = string.IsNullOrWhiteSpace(error)
                                ? "The sales component returned no XML."
                                : error;
                        }
                    }

                    if (result.Loaded)
                    {
                        string jobDirectory = Path.Combine(
                            outputDirectory,
                            SafeSegment(job.DocumentCode),
                            SafeSegment(job.VoucherCode) + "_" + job.VoucherId);
                        Directory.CreateDirectory(jobDirectory);
                        WriteXml(Path.Combine(jobDirectory, "head.xml"), head);
                        WriteXml(Path.Combine(jobDirectory, "body.xml"), body);
                        result.Directory = jobDirectory;
                    }
                }
                catch (Exception exception)
                {
                    result.Error = exception.Message;
                }
                finally
                {
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
            Release(connection);
            Release(commonLogin);
            Release(login);
        }
    }

    private static void Initialize(
        string component,
        object voucher,
        clsLogin login,
        Connection connection,
        object commonLogin,
        U8SpecializedReadJob[] jobs)
    {
        if (component == "purchase")
        {
            var purchaseVoucher = (clsVoucherCO_PU)voucher;
            ClsS_Infor information = null;
            bool positive = true;
            string billType = string.Empty;
            string businessType = jobs.Length == 0 ? string.Empty : (jobs[0].BusinessType ?? string.Empty);
            UseMode useMode = UseMode.CS;
            purchaseVoucher.Init(
                (vouchertype)2,
                ref login,
                ref connection,
                ref information,
                ref positive,
                ref billType,
                ref businessType,
                ref useMode,
                businessType,
                string.Empty);
        }
        else
        {
            dynamic salesVoucher = voucher;
            dynamic salesSystem = Create("USSAServer.clsSystem");
            string error = string.Empty;
            if (!salesSystem.Init(ref login, ref error))
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? "Sales system initialization returned false."
                        : error);
            }
            salesVoucher.clsSys = salesSystem;
        }
    }

    private static dynamic Create(string progId)
    {
        Type type = Type.GetTypeFromProgID(progId, true);
        return Activator.CreateInstance(type);
    }

    private static Connection ConvertConnection(object value)
    {
        IntPtr unknown = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(value);
            return (Connection)Marshal.GetTypedObjectForIUnknown(
                unknown,
                typeof(Connection));
        }
        finally
        {
            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    private static bool XmlHasContent(IXMLDOMDocument2 document)
    {
        return document != null && !string.IsNullOrWhiteSpace(Convert.ToString(document.xml));
    }

    private static void WriteXml(string path, IXMLDOMDocument2 document)
    {
        File.WriteAllText(path, Convert.ToString(document.xml), new UTF8Encoding(false));
    }

    private static string SafeSegment(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
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

    private static void Stage(string outputDirectory, string stage)
    {
        File.WriteAllText(
            Path.Combine(outputDirectory, "native-progress.txt"),
            stage,
            new UTF8Encoding(false));
    }
}
'@

$siteRoot = Split-Path -Parent (Split-Path -Parent $SettingsPath)
$baseInteropDirectory = Join-Path $siteRoot "bin"
$baseInteropAssemblies = @(
    "Interop.U8Login.dll",
    "Interop.MSXML2.dll")
$componentInteropAssemblies = @(
    "ADODB.dll",
    "Info_PU.dll",
    "Scripting.dll",
    "SysInfo.dll",
    "VoucherVerify.dll",
    "Interop.VoucherCO_PU.dll",
    "Interop.VoucherCO_SA.dll")

foreach ($assemblyName in $baseInteropAssemblies) {
    Add-Type -Path (Join-Path $baseInteropDirectory $assemblyName)
}
foreach ($assemblyName in $componentInteropAssemblies) {
    Add-Type -Path (Join-Path $ComponentInteropDirectory $assemblyName)
}

Add-Type -TypeDefinition $helperSource -Language CSharp -ReferencedAssemblies @(
    "Microsoft.CSharp.dll",
    "System.dll",
    "System.Core.dll",
    (Join-Path $baseInteropDirectory "Interop.U8Login.dll"),
    (Join-Path $baseInteropDirectory "Interop.MSXML2.dll"),
    (Join-Path $ComponentInteropDirectory "ADODB.dll"),
    (Join-Path $ComponentInteropDirectory "Info_PU.dll"),
    (Join-Path $ComponentInteropDirectory "Scripting.dll"),
    (Join-Path $ComponentInteropDirectory "SysInfo.dll"),
    (Join-Path $ComponentInteropDirectory "VoucherVerify.dll"),
    (Join-Path $ComponentInteropDirectory "Interop.VoucherCO_PU.dll"),
    (Join-Path $ComponentInteropDirectory "Interop.VoucherCO_SA.dll"))

Write-DiscoveryStage "helper-loaded"

$stored = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$u8Password = Unprotect-Text ([string]$stored.U8PasswordProtected)
if ([string]::IsNullOrWhiteSpace($u8Password)) {
    throw "No usable U8 password is available in the server-local settings."
}

Write-DiscoveryStage "settings-loaded"

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
    $job = New-Object U8SpecializedReadJob
    $job.DocumentCode = [string]$_.documentCode
    $job.VoucherId = [int]$_.voucherId
    $job.VoucherCode = [string]$_.voucherCode
    $job.BusinessType = [string]$_.businessType
    $job
}

if ($jobs.Count -eq 0) {
    throw "No voucher export jobs were supplied."
}

Write-DiscoveryStage "jobs-loaded"

Write-DiscoveryStage "u8-export-started"
$results = [U8SpecializedReadOnlyExporter]::Export(
    $Component,
    $(if ($Component -eq "purchase") { "PU" } else { "SA" }),
    $u8Account,
    [string]$stored.Public.U8Year,
    [string]$stored.Public.U8UserId,
    $u8Password,
    (Get-Date).ToString("yyyy-MM-dd"),
    [string]$stored.Public.U8Server,
    [string]$stored.Public.U8Serial,
    [U8SpecializedReadJob[]]$jobs,
    $OutputDirectory)

Write-DiscoveryStage "u8-export-finished"

[pscustomobject]@{
    succeeded = (@($results | Where-Object { -not $_.Loaded }).Count -eq 0)
    component = $Component
    database = $Database
    exportedAt = (Get-Date).ToString("o")
    readOnly = $true
    results = $results
} | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $OutputDirectory "result.json") -Encoding UTF8

$u8Password = $null
