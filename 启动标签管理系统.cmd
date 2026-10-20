@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start_label_system.ps1"
if errorlevel 1 pause
exit /b %errorlevel%
