# Builds the AnvilLOD SKSE plugin. Two build lines from the same source (like ApocryphaRealm Menu Framework):
#
#   -Line 1     Skyrim SE 1.5.97 up to AE 1.6.1170       (CommonLibSSE-NG, colorglass registry)   <- default
#   -Line 17    newer than 1.6.1170: 1.7.99, 1.7.104...  (CommonLibSSE-NG 7.2.0, cmake\ports-17)
#   -Line both  both of them
#
#   .\build.ps1 -Dest "E:\Tabula Rasa\mods\ANvilLOD"              -> line 1 into <Dest>\SKSE\Plugins\AnvilLOD.dll
#   .\build.ps1 -Line 17 -Dest "D:\Some 1.7 Mod Folder"           -> line 17 there
#   .\build.ps1 -Line both -Dest "E:\Tabula Rasa\mods\ANvilLOD"   -> both into dist\, line 1 also into Dest
#
# Every build also lands in dist\<line>\SKSE\Plugins\AnvilLOD.dll. Each DLL refuses to load on the other
# game version, so the wrong one can never touch the game (skse64.log then lists it as not loaded).
# Uses Visual Studio's bundled vcpkg. Dependencies are installed explicitly because VCPKG_MANIFEST_INSTALL is OFF.

param(
    [string]$Dest,
    [ValidateSet("1", "17", "both")][string]$Line = "1",
    [string]$Vcpkg = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\vcpkg\vcpkg.exe"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Use the vcpkg we install with. A VCPKG_ROOT left over from another setup is only kept if it has the toolchain.
$bundledRoot = Split-Path $Vcpkg
if (-not $env:VCPKG_ROOT -or -not (Test-Path (Join-Path $env:VCPKG_ROOT "scripts\buildsystems\vcpkg.cmake"))) {
    $env:VCPKG_ROOT = $bundledRoot
}
Write-Host "vcpkg: $env:VCPKG_ROOT" -ForegroundColor DarkGray

function Build-Line([string]$which) {
    if ($which -eq "17") {
        $preset = "release-17"; $buildDir = "build-17"; $installDir = "vcpkg_installed-17"; $dist = "dist\1.7.x"
        $label = "game versions newer than 1.6.1170 (CommonLibSSE-NG 7.2.0)"
        $installArgs = @("--x-manifest-root", "$PSScriptRoot\cmake\manifest-17", "--overlay-ports", "$PSScriptRoot\cmake\ports-17")
    } else {
        $preset = "release"; $buildDir = "build"; $installDir = "vcpkg_installed"; $dist = "dist\1.5.97-1.6.1170"
        $label = "game versions 1.5.97 to 1.6.1170"
        $installArgs = @()
    }
    Write-Host "=== $label ===" -ForegroundColor Cyan

    # A failed configure leaves a cache that remembers a bad toolchain path.
    $cache = Join-Path $PSScriptRoot "$buildDir\CMakeCache.txt"
    if (Test-Path $cache) {
        $cached = Select-String -Path $cache -Pattern "^CMAKE_TOOLCHAIN_FILE:.*=(.*)$" | Select-Object -First 1
        if ($cached -and -not (Test-Path $cached.Matches[0].Groups[1].Value.Trim())) {
            Write-Host "Clearing stale CMake cache" -ForegroundColor DarkGray
            Remove-Item -Recurse -Force (Join-Path $PSScriptRoot $buildDir)
        }
    }

    Write-Host "Installing dependencies..." -ForegroundColor Cyan
    & $Vcpkg install --triplet x64-windows-static-md --x-install-root "$PSScriptRoot\$installDir" @installArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "vcpkg install failed ($label) - send the errors above to Claude." }

    Write-Host "Configuring..." -ForegroundColor Cyan
    cmake --preset $preset | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($label) - send the errors above to Claude." }

    Write-Host "Building..." -ForegroundColor Cyan
    cmake --build --preset $preset | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($label) - send the errors above to Claude." }

    $dll = Join-Path $PSScriptRoot "$buildDir\Release\AnvilLOD.dll"
    $pdb = [IO.Path]::ChangeExtension($dll, ".pdb")
    $distDir = Join-Path $PSScriptRoot "$dist\SKSE\Plugins"
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    Copy-Item $dll $distDir -Force
    if (Test-Path $pdb) { Copy-Item $pdb $distDir -Force }
    Write-Host "Built $label -> $distDir" -ForegroundColor Green
    return $dll
}

$lines = if ($Line -eq "both") { @("1", "17") } else { @($Line) }
$built = @{}
foreach ($l in $lines) { $built[$l] = Build-Line $l }

if ($Dest) {
    $copy = if ($Line -eq "both") { "1" } else { $Line }
    $dll = $built[$copy]
    $target = Join-Path $Dest "SKSE\Plugins"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item $dll $target -Force
    $pdb = [IO.Path]::ChangeExtension($dll, ".pdb")
    if (Test-Path $pdb) { Copy-Item $pdb $target -Force }
    Write-Host "Copied the line-$copy DLL to $target" -ForegroundColor Green
}
