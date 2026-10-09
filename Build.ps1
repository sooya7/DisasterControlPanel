param([string]$GamePath = 'E:\SteamLibrary\steamapps\common\Cities Skylines II')
$ErrorActionPreference = 'Stop'
& dotnet build (Join-Path $PSScriptRoot 'src\DisasterControlPanel.csproj') -c Release "-p:GamePath=$GamePath" -p:NuGetAudit=false -v:minimal
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'mod') | Out-Null
$dll = Join-Path $PSScriptRoot 'mod\DisasterControlPanel.dll'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\bin\Release\DisasterControlPanel.dll') -Destination $dll -Force
@{name='DisasterControlPanel'; version='0.2.1'; target='Cities Skylines II 1.6.2f1'; configuration='Release'; sha256=(Get-FileHash -LiteralPath $dll).Hash} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'mod\manifest.json') -Encoding utf8
