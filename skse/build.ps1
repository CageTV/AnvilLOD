# Builds the AnvilLOD SKSE plugin. Two build lines from the same source (like ApocryphaRealm Menu Framework):
#
#   -Line 1     Skyrim SE 1.5.97 up to AE 1.6.1170       (CommonLibSSE-NG, colorglass registry)   <- default
#   -Line 17    newer than 1.6.1170: 1.7.99, 1.7.104...  (CommonLibSSE-NG 7.2.0, cmake\ports-17)
#   -Line vr    Skyrim VR 1.4.15                         (CommonLibSSE-NG 7.2.0 built for VR only, cmake\ports-vr)
#   -Line both  lines 1 and 17
#   -Line all   lines 1, 17 and vr
#
#   .\build.ps1 -Dest "D:\Modlists\MyList\mods\AnvilLOD"              -> line 1 into <Dest>\SKSE\Plugins\AnvilLOD.dll
#   .\build.ps1 -Line 17 -Dest "D:\Some 1.7 Mod Folder"           -> line 17 there
#   .\build.ps1 -Line both -Dest "D:\Modlists\MyList\mods\AnvilLOD"   -> both into dist\, line 1 also into Dest
#
# Every build also lands in dist\<line>\SKSE\Plugins\AnvilLOD.dll. Each DLL refuses to load on the other
# game version, so the wrong one can never touch the game (skse64.log then lists it as not loaded).
# Uses Visual Studio's bundled vcpkg. Dependencies are installed explicitly because VCPKG_MANIFEST_INSTALL is OFF.

param(
    [string]$Dest,
    [ValidateSet("1", "17", "vr", "both", "all")][string]$Line = "1",
    [string]$Vcpkg = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\vcpkg\vcpkg.exe"
)

# "Continue", not "Stop": Windows PowerShell 5.1 turns anything a native tool writes to stderr (vcpkg's harmless
# "builtin-baseline" warning) into a terminating error. Every native call below checks $LASTEXITCODE itself.
$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot

# Use the vcpkg we install with. A VCPKG_ROOT left over from another setup is only kept if it has the toolchain.
$bundledRoot = Split-Path $Vcpkg
if (-not $env:VCPKG_ROOT -or -not (Test-Path (Join-Path $env:VCPKG_ROOT "scripts\buildsystems\vcpkg.cmake"))) {
    $env:VCPKG_ROOT = $bundledRoot
}
Write-Host "vcpkg: $env:VCPKG_ROOT" -ForegroundColor DarkGray

# The compiler stores the source path of every file (assert and log strings, the PDB path) in the DLL. Overwrite the repository
# folder with a same-length filler so the shipped DLLs carry nothing about the machine that built them. Data only, so the
# DLL is otherwise untouched.
Add-Type -TypeDefinition @"
public static class AnvilPathScrub
{
    public static int Replace(byte[] data, byte[] find, byte[] repl)
    {
        int n = 0;
        for (int i = 0; i + find.Length <= data.Length; i++)
        {
            bool match = true;
            for (int k = 0; k < find.Length; k++)
            {
                int a = data[i + k], b = find[k];
                if (a >= 'A' && a <= 'Z') a += 32;
                if (b >= 'A' && b <= 'Z') b += 32;
                if (a != b) { match = false; break; }
            }
            if (!match) continue;
            for (int k = 0; k < find.Length; k++) data[i + k] = repl[k];
            n++;
            i += find.Length - 1;
        }
        return n;
    }
}
"@

function Remove-BuildPaths([string]$file) {
    $root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path.TrimEnd('\')
    $bytes = [IO.File]::ReadAllBytes($file)
    $total = 0
    foreach ($needle in @($root, $root.Replace('\', '/'))) {
        $fill = "src".PadRight($needle.Length, '_')
        foreach ($enc in @([Text.Encoding]::ASCII, [Text.Encoding]::Unicode)) {
            $total += [AnvilPathScrub]::Replace($bytes, $enc.GetBytes($needle), $enc.GetBytes($fill))
        }
    }
    if ($total -gt 0) { [IO.File]::WriteAllBytes($file, $bytes) }
    Write-Host "Removed the build path from $total place(s) in $(Split-Path $file -Leaf)" -ForegroundColor DarkGray
}

function Build-Line([string]$which) {
    if ($which -eq "vr") {
        $preset = "release-vr"; $buildDir = "build-vr"; $installDir = "vcpkg_installed-vr"; $dist = "dist\1.4.15-VR"
        $label = "Skyrim VR 1.4.15 (CommonLibSSE-NG 7.2.0, VR only)"
        $installArgs = @("--x-manifest-root", "$PSScriptRoot\cmake\manifest-vr", "--overlay-ports", "$PSScriptRoot\cmake\ports-vr")
    } elseif ($which -eq "17") {
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
    # Through cmd so vcpkg's stderr (a harmless baseline warning) is merged by the OS and PowerShell never sees an error record.
    $vcpkgArgs = (@("install", "--triplet", "x64-windows-static-md", "--x-install-root", "`"$PSScriptRoot\$installDir`"") + ($installArgs | ForEach-Object { "`"$_`"" })) -join " "
    cmd /c "`"$Vcpkg`" $vcpkgArgs 2>&1" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "vcpkg install failed ($label) - send the errors above to Claude." }

    Write-Host "Configuring..." -ForegroundColor Cyan
    cmake --preset $preset | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($label) - send the errors above to Claude." }

    Write-Host "Building..." -ForegroundColor Cyan
    cmake --build --preset $preset | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($label) - send the errors above to Claude." }

    $dll = Join-Path $PSScriptRoot "$buildDir\Release\AnvilLOD.dll"
    Remove-BuildPaths $dll
    $pdb = [IO.Path]::ChangeExtension($dll, ".pdb")
    $distDir = Join-Path $PSScriptRoot "$dist\SKSE\Plugins"
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    Copy-Item $dll $distDir -Force
    if (Test-Path $pdb) { Copy-Item $pdb $distDir -Force }
    Write-Host "Built $label -> $distDir" -ForegroundColor Green
    return $dll
}

$lines = switch ($Line) { "both" { @("1", "17") } "all" { @("1", "17", "vr") } default { @($Line) } }
$built = @{}
foreach ($l in $lines) { $built[$l] = Build-Line $l }

if ($Dest) {
    $copy = if ($Line -in "both", "all") { "1" } else { $Line }
    $dll = $built[$copy]
    $target = Join-Path $Dest "SKSE\Plugins"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item $dll $target -Force
    $pdb = [IO.Path]::ChangeExtension($dll, ".pdb")
    if (Test-Path $pdb) { Copy-Item $pdb $target -Force }
    Write-Host "Copied the line-$copy DLL to $target" -ForegroundColor Green
}
