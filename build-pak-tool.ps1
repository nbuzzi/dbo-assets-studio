<#
.SYNOPSIS
  Builds native\pak-tool.exe (x86), the helper that reads/writes the encrypted PAK index.

.DESCRIPTION
  pak-tool.cpp uses the serializer (CNtlFileSerializer) and MD5 helpers that ship with
  the Dragon Ball Online client source. That source is NOT part of this repository and
  is not redistributable: you must provide your own copy.

  The PAK key is NOT compiled in. pak-tool reads it at runtime from the DBO_PAK_KEY
  environment variable, which DBO Assets Studio fills from your local studio.config.json.

.PARAMETER UtilPath
  Folder containing NtlFileSerializer.h, md5.h and the x86 Util.lib (from your own client source).

.PARAMETER OutputDirectory
  Where pak-tool.exe is written (default: .\native\bin).

.PARAMETER VcVarsAll
  Path to vcvarsall.bat. Auto-detected through vswhere when omitted.
#>
param(
    [Parameter(Mandatory)][string]$UtilPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'native\bin'),
    [string]$VcVarsAll
)
$ErrorActionPreference = 'Stop'
$util = (Resolve-Path -LiteralPath $UtilPath).Path
foreach ($required in 'NtlFileSerializer.h', 'md5.h') {
    if (-not (Test-Path -LiteralPath (Join-Path $util $required))) { throw "Missing $required in $util" }
}
$library = Get-ChildItem -LiteralPath $util -Recurse -Filter 'Util.lib' | Where-Object { $_.FullName -match 'Win32|x86' } | Select-Object -First 1
if (-not $library) { throw "x86 Util.lib not found under $util (build the client Util project for Win32 first)." }

if (-not $VcVarsAll) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found. Install Visual Studio Build Tools (C++ workload) or pass -VcVarsAll.' }
    $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $VcVarsAll = Join-Path $install 'VC\Auxiliary\Build\vcvarsall.bat'
}
if (-not (Test-Path -LiteralPath $VcVarsAll)) { throw "vcvarsall.bat not found: $VcVarsAll" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$source = Join-Path $PSScriptRoot 'native\pak-tool.cpp'
$exe = Join-Path $OutputDirectory 'pak-tool.exe'
$obj = Join-Path $OutputDirectory 'pak-tool.obj'
$cmd = "`"$VcVarsAll`" x86 && cl /nologo /EHsc /MD /O2 /D_CRT_SECURE_NO_WARNINGS /I`"$util`" `"$source`" /Fo`"$obj`" /Fe`"$exe`" /link `"$($library.FullName)`""
cmd.exe /c $cmd
if ($LASTEXITCODE -ne 0) { throw "pak-tool build failed ($LASTEXITCODE)." }
Write-Host "Built $exe"
