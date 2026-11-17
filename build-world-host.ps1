<#
.SYNOPSIS
  Builds native\bin\world-host\world-host.exe (x86), the World Editor host that runs the
  client's real world engine (NtlPresentation) in tool mode, embedded in the Studio.

.DESCRIPTION
  world-host.cpp links the client's engine libraries, so it needs YOUR OWN client source tree
  (not part of this repository, not redistributable). NtlPresentation must be built with the
  Release4Tools configuration, which defines dNTL_WORLD_TOOL_MODE (the editing code paths of the
  original engine). -BuildToolLibs builds those libraries with MSBuild.

.PARAMETER ClientSource
  Root of your client source tree: the folder that contains DBO-Client-2.0\, NtlLib\ and DboShared\.
  DBO-Client-2.0\Release must already contain the regular Release libs (NtlCore, NtlGui, ...) and
  DBO-Client-2.0\Renderware the RenderWare SDK.

.PARAMETER ToolSource
  Optional: a second source tree whose NtlPresentation has the Release4Tools (tool mode) configuration,
  when it is not the same tree as -ClientSource. Engine headers and Release4Tools libs come from here;
  RenderWare and the regular Release libs come from -ClientSource.

.PARAMETER DirectXRoot
  DirectX 9 SDK (June 2010) folder with Include\ and Lib\x86\.

.PARAMETER ClientRoot
  Optional: your installed client, to copy fmod.dll, D3DX9_43.dll, DevIL.dll and BugTrap.dll next to the exe.

.PARAMETER BuildToolLibs
  Build NtlPresentation and its sibling libs in Release4Tools before linking.
#>
param(
    [Parameter(Mandatory)][string]$ClientSource,
    [Parameter(Mandatory)][string]$DirectXRoot,
    [string]$ToolSource,
    [string]$ClientRoot,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'native\bin\world-host'),
    [switch]$BuildToolLibs,
    [string]$VcVarsAll
)
$ErrorActionPreference = 'Stop'
$core = (Resolve-Path -LiteralPath $ClientSource).Path
$client = Join-Path $core 'DBO-Client-2.0'
$toolCore = if ($ToolSource) { (Resolve-Path -LiteralPath $ToolSource).Path } else { $core }
$toolClient = Join-Path $toolCore 'DBO-Client-2.0'
$proj = Join-Path $toolClient 'Lib\NtlPresentation\NtlPresentation.vcxproj'
if (-not (Test-Path -LiteralPath $proj)) { throw "NtlPresentation.vcxproj not found under $toolClient" }
if ((Get-Content -LiteralPath $proj -Raw) -notmatch 'dNTL_WORLD_TOOL_MODE') { throw 'This NtlPresentation project has no Release4Tools configuration with dNTL_WORLD_TOOL_MODE (see -ToolSource).' }
$directX = (Resolve-Path -LiteralPath $DirectXRoot).Path

$install = $null
if (-not $VcVarsAll -or $BuildToolLibs) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere.exe not found. Install Visual Studio (C++ workload, MSVC v142 x86).' }
    $install = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $VcVarsAll) { $VcVarsAll = Join-Path $install 'VC\Auxiliary\Build\vcvarsall.bat' }
}

if ($BuildToolLibs -or -not (Test-Path -LiteralPath (Join-Path $toolClient 'Release4Tools\NtlPresentation.lib'))) {
    if (-not $install) { throw 'Run with -BuildToolLibs and without -VcVarsAll so MSBuild can be located, or build Release4Tools yourself.' }
    $msbuild = Join-Path $install 'MSBuild\Current\Bin\MSBuild.exe'
    foreach ($p in 'NtlPresentation', 'Ntl_Plugin_World', 'NtlFramework', 'NtlGui', 'NtlSound', 'NtlLocalize', 'NtlFlasher') {
        & $msbuild (Join-Path $toolClient "Lib\$p\$p.vcxproj") -t:Build -m -nr:false -nologo -v:minimal `
            -p:Configuration=Release4Tools -p:Platform=Win32 -p:PlatformToolset=v142 "-p:SolutionDir=$toolClient\" "-p:DXSDK_DIR=$directX\"
        if ($LASTEXITCODE -ne 0) { throw "$p Release4Tools build failed ($LASTEXITCODE)" }
    }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$buildDir = Join-Path $PSScriptRoot 'obj\world-host'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$includes = @(
    (Join-Path $toolClient 'Lib\NtlPresentation'), (Join-Path $toolClient 'Lib\NtlCore'), (Join-Path $toolClient 'Lib\NtlFramework'),
    (Join-Path $toolClient 'Lib\NtlGui'), (Join-Path $toolClient 'Lib\NtlSound'), (Join-Path $toolClient 'Lib\NtlLocalize'),
    (Join-Path $toolClient 'Lib\Ntl_Plugin_Collis\collis'), (Join-Path $toolClient 'Lib\Ntl_Plugin_Toon\toon'), (Join-Path $toolClient 'Lib\Ntl_Plugin_World\driver\d3d9'),
    (Join-Path $client 'Renderware\Include'),
    (Join-Path $toolCore 'NtlLib\Shared\NtlXMLLoader'), (Join-Path $toolCore 'NtlLib\Shared'), (Join-Path $toolCore 'NtlLib\Shared\Util'),
    (Join-Path $toolCore 'NtlLib\Shared\NtlTrigger'), (Join-Path $toolCore 'DboShared\NtlShared2'), (Join-Path $toolCore 'DboShared\NtlGameTable'),
    (Join-Path $directX 'Include')
) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { "/I`"$_`"" }
function ClientLib([string]$name) {
    foreach ($c in (Join-Path $toolClient "Release4Tools\$name.lib"), (Join-Path $client "Release\$name.lib")) { if (Test-Path -LiteralPath $c) { return $c } }
    return (Join-Path $client "Release\$name.lib")
}
$libs = @(Join-Path $toolClient 'Release4Tools\NtlPresentation.lib') +
    (@('NtlCore', 'NtlFramework', 'NtlGui', 'NtlSound', 'NtlLocalize', 'NtlFlasher', 'rpcollis', 'rptoon', 'rpworld') | ForEach-Object { ClientLib $_ }) +
    @((Join-Path $toolClient 'BugTrap\BugTrap.lib')) +
    (@('rwcore', 'rwplcore', 'rtanim', 'rtworld', 'rpspline', 'rpprtstd', 'rpprtadv', 'rpptank', 'rtpick', 'rtray', 'rtintsec', 'rtfsyst', 'rtpng', 'rtbmp', 'rtdict', 'rpuvanim', 'rphanim', 'rpskin', 'rpmatfx', 'rpusrdat', 'rpid', 'rpltmap', 'rppvs', 'rtimport', 'rtgcond', 'rtquat', 'rtltmap', 'rtcharse', 'rtwing', 'rtbary', 'rprandom', 'rpmipkl') | ForEach-Object { Join-Path $client "Renderware\Lib\Release\$_.lib" }) +
    (@('dxguid', 'd3d9', 'd3dx9') | ForEach-Object { Join-Path $directX "Lib\x86\$_.lib" }) +
    @((Join-Path $core 'NtlLib\Shared\Util\Release\Win32\Util.lib'), (Join-Path $core 'NtlLib\Shared\NtlXMLLoader\Release\Win32\NtlXMLLoader.lib'),
      (Join-Path $core 'NtlLib\Shared\NtlTrigger\Release\Win32\NtlTrigger.lib'), (Join-Path $core 'NtlLib\Shared\Lua\Release\Win32\Lua.lib'),
      (Join-Path $client 'Lib\NtlSound\fmod\fmod_vc.lib'),
      (Join-Path $client 'Lib\NtlFlasher\GFxSDK3.3\Lib\Win32\Msvc80\Release\libgfx.lib'), (Join-Path $client 'Lib\NtlFlasher\GFxSDK3.3\Lib\Win32\Msvc80\Release\libgrenderer_d3d9.lib'),
      (Join-Path $core 'NtlLib\Shared\Zip\zlib123\projects\Release\Win32\zlib.lib'), (Join-Path $client 'Lib\NtlFlasher\GFxSDK3.3\3rdParty\jpeg-6b\lib\Win32\Msvc80\Release\libjpeg.lib'))
foreach ($l in $libs) { if (-not (Test-Path -LiteralPath $l)) { Write-Warning "Missing $l" } }
$libs = $libs | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { "`"$_`"" }
$sys = 'ddraw.lib winmm.lib ws2_32.lib advapi32.lib ole32.lib oleaut32.lib uuid.lib comdlg32.lib gdi32.lib version.lib user32.lib shell32.lib shlwapi.lib imm32.lib comctl32.lib dbghelp.lib legacy_stdio_definitions.lib'
$src = Join-Path $PSScriptRoot 'native\world-host.cpp'
$exe = Join-Path $OutputDirectory 'world-host.exe'
$compileRsp = Join-Path $buildDir 'compile.rsp'; $linkRsp = Join-Path $buildDir 'link.rsp'
"/nologo /c /EHsc /MD /O2 /Z7 /std:c++17 /DWIN32 /DNDEBUG /D_CRT_SECURE_NO_DEPRECATE /D_CRT_SECURE_NO_WARNINGS /D_HAS_AUTO_PTR_ETC=1 /DNOMINMAX /DdNTL_WORLD_TOOL_MODE=\`"DEV\`" $($includes -join ' ') /Fo`"$buildDir\world-host.obj`" `"$src`"" | Set-Content -LiteralPath $compileRsp -Encoding ascii
"/NOLOGO /DEBUG /OPT:REF /MACHINE:X86 /SUBSYSTEM:CONSOLE /NODEFAULTLIB:LIBCMT /OUT:`"$exe`" /PDB:`"$buildDir\world-host.pdb`" `"$buildDir\world-host.obj`" $($libs -join ' ') $sys" | Set-Content -LiteralPath $linkRsp -Encoding ascii
$cmdPath = Join-Path $buildDir 'build.cmd'
@("call `"$VcVarsAll`" x86 -vcvars_ver=14.29 >nul", 'if errorlevel 1 exit /b %errorlevel%',
  "cl @`"$compileRsp`"", 'if errorlevel 1 exit /b %errorlevel%', "link @`"$linkRsp`"", 'exit /b %errorlevel%') -join "`r`n" | Set-Content -LiteralPath $cmdPath -Encoding ascii
& $env:ComSpec /d /s /c "`"$cmdPath`""
if ($LASTEXITCODE -ne 0) { throw "world-host build failed ($LASTEXITCODE). See $cmdPath" }
if ($ClientRoot) {
    foreach ($dep in 'fmod.dll', 'D3DX9_43.dll', 'DevIL.dll', 'BugTrap.dll') {
        $source = Join-Path $ClientRoot $dep
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $OutputDirectory $dep) -Force }
    }
}
Write-Host "world-host.exe: $exe"
