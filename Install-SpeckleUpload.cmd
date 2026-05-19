@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在以 Bypass 策略启动安装脚本...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SpeckleUpload.ps1"
if errorlevel 1 pause
