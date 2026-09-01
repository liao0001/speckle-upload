@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在部署到 Revit 2026 插件目录...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SpeckleUpload.ps1" -RevitYear 2026
exit /b %ERRORLEVEL%
