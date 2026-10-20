[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath('D:\NewLabelPrintDevelopment')
if ($root -ne 'D:\NewLabelPrintDevelopment') { throw 'Unexpected removal target.' }

if (Get-ScheduledTask -TaskName 'NewLabelPrintDevelopment' -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName 'NewLabelPrintDevelopment' -ErrorAction SilentlyContinue
}
Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and
    [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

if ($PSCmdlet.ShouldProcess($root, 'Remove the obsolete server-hosted printing web app')) {
    Unregister-ScheduledTask -TaskName 'NewLabelPrintDevelopment' -Confirm:$false -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -Name 'NewLabelPrintDevelopment-18003' -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

[pscustomobject]@{
    rootExists = Test-Path -LiteralPath $root
    taskExists = [bool](Get-ScheduledTask -TaskName 'NewLabelPrintDevelopment' -ErrorAction SilentlyContinue)
    firewallExists = [bool](Get-NetFirewallRule -Name 'NewLabelPrintDevelopment-18003' -ErrorAction SilentlyContinue)
}
