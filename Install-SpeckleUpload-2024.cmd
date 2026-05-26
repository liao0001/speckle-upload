@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在部署到 Revit 2024 插件目录...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SpeckleUpload.ps1" -RevitYear 2024
if errorlevel 1 pause
