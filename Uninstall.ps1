[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (Get-Process Cities2 -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited }) { throw '请先退出游戏。建议先停止本面板触发的灾难。' }
$gameData = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData') + 'Low') 'Colossal Order\Cities Skylines II'))
$deploy = [IO.Path]::GetFullPath((Join-Path $gameData 'Mods\DisasterControlPanel'))
if (!(Test-Path -LiteralPath $deploy)) { Write-Host '未安装灾难控制面板。'; return }
if ($deploy -ne (Join-Path $gameData 'Mods\DisasterControlPanel')) { throw '目标路径检查失败。' }
if ((Get-Item -LiteralPath $deploy).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '目标是目录链接，停止操作。' }
$backupRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DisasterControlPanel\uninstalled'
$backup = [IO.Path]::GetFullPath((Join-Path $backupRoot (Get-Date -Format 'yyyyMMdd-HHmmss-fff')))
if (!$backup.StartsWith(($backupRoot + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw '备份路径检查失败。' }
New-Item -ItemType Directory -Force -Path (Split-Path $backup -Parent) | Out-Null
Move-Item -LiteralPath $deploy -Destination $backup
Write-Host "已卸载，原文件可从这里恢复：$backup"
