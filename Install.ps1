[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (Get-Process Cities2 -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited }) { throw '请先退出《城市：天际线 II》，再安装。' }
$gameData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData') + 'Low') 'Colossal Order\Cities Skylines II'
$deploy = [IO.Path]::GetFullPath((Join-Path $gameData 'Mods\DisasterControlPanel'))
$source = Join-Path $PSScriptRoot 'mod\DisasterControlPanel.dll'
if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw '安装包缺少 mod\DisasterControlPanel.dll。' }
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mod\manifest.json') -Raw | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ($hash -ne $manifest.sha256) { throw 'DLL 校验失败，停止安装。' }
if (Test-Path -LiteralPath $deploy) {
    $item = Get-Item -LiteralPath $deploy
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '目标是目录链接，停止以免修改其他位置。' }
    $backup = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('DisasterControlPanel\backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    Copy-Item -LiteralPath $deploy -Destination $backup -Recurse
    Write-Host "原版本备份：$backup"
}
New-Item -ItemType Directory -Force -Path $deploy | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $deploy 'DisasterControlPanel.dll') -Force
if ((Get-FileHash -LiteralPath (Join-Path $deploy 'DisasterControlPanel.dll')).Hash -ne $hash) { throw '安装后校验失败。' }
Write-Host "已安装灾难控制面板 $($manifest.version)。进入城市后点击右下角“灾难”，或按 F9。"
