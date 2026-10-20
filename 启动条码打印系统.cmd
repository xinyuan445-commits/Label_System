@echo off
setlocal

set "launcher=%LOCALAPPDATA%\NewLabelPrintSystem\Start-LabelPrintSystem.ps1"
if not exist "%launcher%" (
    echo Barcode printing system is not installed on this computer.
    echo Run New_Label_Print_System\deployment\Install-LabelSystemLocal.ps1 first.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%launcher%"
if errorlevel 1 pause
exit /b %errorlevel%
