"""Authenticated download of the project-built Windows client printing assistant."""

from __future__ import annotations

import json
from io import BytesIO
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

from flask import Blueprint, current_app, jsonify, request, send_file
from flask.typing import ResponseReturnValue
from flask_login import login_required

from app.auth import labelsystem
from app.services.labelsystem_transport import IntegrationError, invoke

print_assistant_bp = Blueprint("print_assistant", __name__)


@print_assistant_bp.get("/print-assistant/windows")
@login_required
def download_windows_assistant() -> ResponseReturnValue:
    root = Path(__file__).resolve().parents[3]
    binary = Path(
        current_app.config.get(
            "PRINT_ASSISTANT_BINARY", root / "connector" / "dist" / "blg-connector-windows.exe"
        )
    )
    if not binary.is_file():
        return jsonify({"error": "assistant_not_built"}), 503
    config = "client_only: true\nserver_url: " + json.dumps(request.host_url.rstrip("/")) + "\n"
    if labelsystem.enabled():
        try:
            capabilities = invoke("GET", "/api/v1/print/capabilities")
        except IntegrationError as exc:
            return jsonify({"error": exc.code, "detail": exc.message}), exc.status
        if not capabilities.get("serverPublicKey"):
            return jsonify({"error": "assistant_server_key_unavailable"}), 503
        config += (
            "disable_printing: "
            + (
                "false"
                if capabilities.get("allowNewDispatch") and not capabilities.get("simulationOnly")
                else "true"
            )
            + "\n"
        )
        config += "v2_server_public_key: " + json.dumps(capabilities["serverPublicKey"]) + "\n"
    script = r'''$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'LabelPrintingAssistant'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$exePath = Join-Path $installDir 'blg-connector.exe'
Get-CimInstance Win32_Process -Filter "Name='blg-connector.exe'" | Where-Object {
    $_.ExecutablePath -eq $exePath
} | ForEach-Object {
    $running = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
    if ($running) {
        Stop-Process -Id $running.Id -Force
        if (-not $running.WaitForExit(10000)) { throw 'Printing assistant did not exit.' }
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'blg-connector.exe') -Destination $exePath -Force
$configPath = Join-Path $installDir 'config.yaml'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config.yaml') -Destination $configPath -Force
if ((Get-Content -LiteralPath $configPath -Raw) -match '(?m)^v2_server_public_key:') {
    $keyPath = Join-Path $installDir 'device-v2.key'
    $pairingPath = Join-Path $installDir 'device-v2.json'
    if (-not (Test-Path -LiteralPath $pairingPath)) {
        if (Test-Path -LiteralPath $keyPath) {
            throw 'Device key exists. Restore its pairing file before reinstalling.'
        }
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = $exePath
        $start.Arguments = '-init-v2 "' + $keyPath + '"'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.StandardOutputEncoding = [Text.Encoding]::UTF8
        $init = [Diagnostics.Process]::Start($start)
        $identity = $init.StandardOutput.ReadToEnd()
        $init.WaitForExit()
        if ($init.ExitCode -ne 0) { throw 'Device initialization failed.' }
        $null = $identity | ConvertFrom-Json
        [IO.File]::WriteAllText($pairingPath, $identity, (New-Object Text.UTF8Encoding($false)))
    }
    if (-not (Test-Path -LiteralPath $keyPath)) { throw 'Device private key is missing.' }
    $pair = Get-Content -LiteralPath $pairingPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $deviceConfig = @(
        ('v2_device_id: ' + ($pair.id | ConvertTo-Json -Compress)),
        ('v2_private_key_path: ' + ($keyPath | ConvertTo-Json -Compress)))
    Add-Content -LiteralPath $configPath -Value $deviceConfig -Encoding UTF8
    $userGrant = '*' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value + ':(OI)(CI)F'
    & icacls $installDir /inheritance:r /grant:r $userGrant `
        '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Device key permissions could not be secured.' }
}
$startupPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'LabelPrintingAssistant.vbs'
$command = '"""' + $exePath + '"" -config ""' + $configPath + '"""'
$vbs = 'CreateObject("WScript.Shell").Run ' + $command + ', 0, False'
[IO.File]::WriteAllText($startupPath, $vbs, [Text.Encoding]::Unicode)
Start-Process -FilePath $exePath -ArgumentList ('-config "' + $configPath + '"') `
    -WorkingDirectory $installDir -WindowStyle Hidden
$desktopEntry = Join-Path ([Environment]::GetFolderPath('Desktop')) '标签打印系统.url'
[IO.File]::WriteAllText($desktopEntry, "[InternetShortcut]`r`nURL=http://127.0.0.1:9110/app/`r`n")
Write-Host '安装完成。请双击桌面“标签打印系统”进入。'
Read-Host '按回车关闭'
'''
    buffer = BytesIO()
    with ZipFile(buffer, "w", ZIP_DEFLATED) as archive:
        archive.writestr("blg-connector.exe", binary.read_bytes())
        archive.writestr("config.yaml", config)
        archive.writestr("install.ps1", script.encode("utf-8-sig"))
        archive.writestr(
            "install.bat",
            '@echo off\r\ncd /d "%~dp0"\r\n'
            "powershell.exe -NoProfile -ExecutionPolicy Bypass "
            '-File "%~dp0install.ps1"\r\nif errorlevel 1 pause\r\n',
        )
    buffer.seek(0)
    response = send_file(
        buffer,
        mimetype="application/zip",
        as_attachment=True,
        download_name="LabelPrintingAssistant-Windows.zip",
    )
    response.headers["Cache-Control"] = "no-store"
    return response
