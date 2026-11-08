<#
.SYNOPSIS
  Publishes DBO Assets Studio (desktop app + command line tool).

.PARAMETER OutputDirectory
  Where to write the portable build (default: .\dist).

.PARAMETER PakTool
  Optional path to a pak-tool.exe built with build-pak-tool.ps1. It is copied next to the app.

.NOTES
  Requires the .NET 8 SDK. The web dependencies (Three.js, Ruffle) are downloaded once and
  hash-verified by prepare-web.py (Python 3).
  Your PAK key is read at runtime from studio.config.json -- it is never part of the build.
#>
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [string]$PakTool
)
$ErrorActionPreference = 'Stop'
$out = [IO.Path]::GetFullPath($OutputDirectory)

dotnet publish (Join-Path $PSScriptRoot 'DboAssetsStudio.csproj') -c Release -r win-x64 --self-contained true -o $out -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
dotnet publish (Join-Path $PSScriptRoot 'cli\DboAssetsCli.csproj') -c Release -r win-x64 --self-contained true -o $out -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }

python (Join-Path $PSScriptRoot 'prepare-web.py') --output $out
if ($LASTEXITCODE -ne 0) { throw 'Web dependency preparation failed.' }

if ($PakTool) {
    Copy-Item -LiteralPath $PakTool -Destination (Join-Path $out 'pak-tool.exe') -Force
} else {
    Write-Warning 'pak-tool.exe not copied. Build it with build-pak-tool.ps1 (needs your own DBO client source) and pass -PakTool.'
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'studio.config.example.json') -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'LICENSE') -Destination $out -Force
Write-Host "Done: $out"
