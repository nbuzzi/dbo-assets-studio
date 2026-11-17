<#
.SYNOPSIS
  Builds native\bin\ui-render.exe (x86), the UI Maker renderer.

.DESCRIPTION
  ui-render.cpp links the client's own GUI libraries (NtlGui, NtlFlasher/GFx 3.3, NtlCore,
  NtlSound) and RenderWare, so windows are drawn exactly like the game draws them.
  Those sources and libraries are NOT part of this repository and are not redistributable:
  you must provide your own client source tree, already built in Release (Win32).

.PARAMETER ClientSource
  Root of your client source tree: the folder that contains DBO-Client-2.0\ and NtlLib\.
  DBO-Client-2.0\Release must contain NtlGui.lib, NtlFlasher.lib, NtlCore.lib and NtlSound.lib.

.PARAMETER DirectXRoot
  DirectX 9 SDK (June 2010) folder with Include\ and Lib\x86\.

.PARAMETER ClientRoot
  Optional: your installed client, to copy fmod.dll and D3DX9_43.dll next to the exe.

.PARAMETER VcVarsAll
  Path to vcvarsall.bat. Auto-detected through vswhere when omitted. The MSVC 14.29 (v142)
  x86 toolset is used, like the client.
#>
param(
    [Parameter(Mandatory)][string]$ClientSource,
    [Parameter(Mandatory)][string]$DirectXRoot,
    [string]$ClientRoot,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'native\bin'),
    [string]$VcVarsAll
)
$ErrorActionPreference = 'Stop'
$core = (Resolve-Path -LiteralPath $ClientSource).Path
$client = Join-Path $core 'DBO-Client-2.0'
foreach ($lib in 'NtlGui.lib', 'NtlFlasher.lib', 'NtlCore.lib', 'NtlSound.lib') {
    if (-not (Test-Path -LiteralPath (Join-Path $client "Release\$lib"))) { throw "Missing $lib in $client\Release. Build the client (Release, Win32) first." }
}
$directX = (Resolve-Path -LiteralPath $DirectXRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $directX 'Include\d3dx9.h'))) { throw "DirectX 9 SDK not found in $directX" }

if (-not $VcVarsAll) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found. Install Visual Studio (C++ workload, MSVC v142 x86) or pass -VcVarsAll.' }
    $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $VcVarsAll = Join-Path $install 'VC\Auxiliary\Build\vcvarsall.bat'
}
if (-not (Test-Path -LiteralPath $VcVarsAll)) { throw "vcvarsall.bat not found: $VcVarsAll" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$buildDir = Join-Path $PSScriptRoot 'obj\ui-render'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$rw = Join-Path $client 'Renderware'
$gfx = Join-Path $client 'Lib\NtlFlasher\GFxSDK3.3'
$fmod = Join-Path $client 'Lib\NtlSound\fmod'
$includes = @(
    (Join-Path $directX 'Include'), (Join-Path $rw 'Include'),
    (Join-Path $client 'Lib\NtlGui'), (Join-Path $client 'Lib\NtlCore'), (Join-Path $client 'Lib\NtlFlasher'), (Join-Path $client 'Lib\NtlSound'),
    (Join-Path $gfx 'Include'), (Join-Path $gfx 'Src\GRenderer'), (Join-Path $gfx 'Src\GKernel'), (Join-Path $gfx 'Src\GFxXML'),
    (Join-Path $core 'NtlLib\Shared\Util'), (Join-Path $core 'NtlLib\Shared'), $fmod
) | ForEach-Object { "/I`"$_`"" }
$libpaths = @((Join-Path $client 'Release'), (Join-Path $rw 'Lib\Release'), (Join-Path $directX 'Lib\x86'), $fmod,
              (Join-Path $gfx 'Lib\Win32\Msvc80\Release'), (Join-Path $gfx '3rdParty\jpeg-6b\lib\Win32\Msvc80\Release'),
              (Join-Path $core 'NtlLib\Shared\Zip\zlib123\projects\Release\Win32')) | ForEach-Object { "/LIBPATH:`"$_`"" }
$libs = 'NtlGui.lib NtlFlasher.lib libgfx.lib libgrenderer_d3d9.lib zlib.lib libjpeg.lib NtlCore.lib NtlSound.lib rpworld.lib rtfsyst.lib rtpng.lib rtbmp.lib fmod_vc.lib user32.lib gdi32.lib winmm.lib imm32.lib d3d9.lib d3dx9.lib shell32.lib ole32.lib ddraw.lib dxguid.lib advapi32.lib ws2_32.lib legacy_stdio_definitions.lib'
$src = Join-Path $PSScriptRoot 'native\ui-render.cpp'
$exe = Join-Path $OutputDirectory 'ui-render.exe'
$cmdPath = Join-Path $buildDir 'build.cmd'
@(
    "call `"$VcVarsAll`" x86 -vcvars_ver=14.29 >nul",
    'if errorlevel 1 exit /b %errorlevel%',
    "cl /nologo /EHsc /MD /O2 /Z7 /std:c++14 /DWIN32 /D_WINDOWS /DNDEBUG $($includes -join ' ') /Fo`"$buildDir\ui-render.obj`" /Fe`"$exe`" `"$src`" /link /DEBUG /PDB:`"$buildDir\ui-render.pdb`" /NODEFAULTLIB:LIBCMT $($libpaths -join ' ') $libs",
    'exit /b %errorlevel%'
) -join "`r`n" | Set-Content -LiteralPath $cmdPath -Encoding ascii
& $env:ComSpec /d /s /c "`"$cmdPath`""
if ($LASTEXITCODE -ne 0) { throw "ui-render build failed ($LASTEXITCODE). See $cmdPath" }

if ($ClientRoot) {
    foreach ($dep in 'fmod.dll', 'D3DX9_43.dll') {
        $source = Join-Path $ClientRoot $dep
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $OutputDirectory $dep) -Force }
        else { Write-Warning "$dep not found in $ClientRoot; ui-render.exe needs it at run time." }
    }
}
Write-Host "ui-render.exe: $exe"
