@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\NewLabelPrintSystem\Start-LabelPrintSystem.ps1"
if errorlevel 1 pause
