#Requires -Version 5.1
# 若直接运行本脚本被系统拦截（执行策略），请双击同目录下的 Install-SpeckleUpload.cmd，
# 或在 PowerShell 中执行: powershell -NoProfile -ExecutionPolicy Bypass -File ".\Install-SpeckleUpload.ps1"
<#
.SYNOPSIS
  将当前目录（解压后的 SpeckleUpload 制品）部署到 Revit 用户插件目录。

.PARAMETER RevitYear
  Revit 版本年号：2022、2024 或 2026。默认 2022。

.DESCRIPTION
  目标目录：%APPDATA%\Autodesk\Revit\Addins\{RevitYear}\SpeckleUpload
#>

param(
  [ValidateSet("2022", "2024", "2026")]
  [string]$RevitYear = "2022"
)

$ErrorActionPreference = "Stop"

# 源目录 = 本脚本所在目录（绝对路径）
$SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($SourceDir)) {
  $SourceDir = $PSScriptRoot
}

# 目标插件目录（与说明.md 中示例一致：Roaming\...\Addins\{year}\SpeckleUpload）
$PluginDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear\SpeckleUpload"

# 本脚本完整路径，移动时排除自身
$ThisScriptPath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ThisScriptPath)) {
  $ThisScriptPath = $PSCommandPath
}

Write-Host "Revit 版本: $RevitYear"
Write-Host "源目录: $SourceDir"
Write-Host "目标目录: $PluginDir"
Write-Host ""

# 2. 删除目标插件目录下全部内容（若目录不存在则创建空目录）
if (Test-Path -LiteralPath $PluginDir) {
  Write-Host "[1/4] 正在清空插件目录..."
  Remove-Item -LiteralPath $PluginDir -Recurse -Force
}
Write-Host "[2/4] 正在创建插件目录..."
New-Item -ItemType Directory -Path $PluginDir -Force | Out-Null

# 3. 将源目录下除本脚本外的所有项移动到插件目录
Write-Host "[3/4] 正在从源目录移动文件到插件目录（保留本脚本在源目录）..."
$items = Get-ChildItem -LiteralPath $SourceDir -Force | Where-Object {
  $_.FullName -ne $ThisScriptPath
}

if (-not $items) {
  Write-Warning "源目录中没有可移动的文件（除脚本外）。请确认已解压制品 zip。"
} else {
  foreach ($item in $items) {
    $dest = Join-Path $PluginDir $item.Name
    Move-Item -LiteralPath $item.FullName -Destination $dest -Force
  }
}

# 4. Unblock（与说明.md 中命令等价）
Write-Host "[4/4] 正在解除锁定 Unblock-File..."
Get-ChildItem -Path $PluginDir -Recurse -File -ErrorAction SilentlyContinue | Unblock-File

Write-Host ""
Write-Host "========================================"
Write-Host " 版本替换完成"
Write-Host "========================================"
Write-Host "请完全退出 Revit 后重新打开以加载新版本（若 Revit 正在运行）。"
Write-Host ""
cmd /c pause
