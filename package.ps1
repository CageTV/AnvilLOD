# Builds the two release downloads into .\release:
#
#   AnvilLOD-<version>.zip                 the tool (app + command line), unzip anywhere and run AnvilLOD.App.exe
#   AnvilLOD SKSE Plugin-<version>.zip     FOMOD for MO2/Vortex: asks for the game version and installs the
#                                          matching AnvilLOD.dll (SE 1.5.97 - AE 1.6.1170, or newer than 1.6.1170)
#
#   .\package.ps1                  build everything, then package
#   .\package.ps1 -SkipBuild       package what's already in dist\ and skse\dist\

param([switch]$SkipBuild)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$version = ([xml](Get-Content "Directory.Build.props")).Project.PropertyGroup.Version
if (-not $version) { throw "No <Version> in Directory.Build.props" }
Write-Host "AnvilLOD $version" -ForegroundColor Cyan

$toolDist = Join-Path $PSScriptRoot "dist\tool"
if (-not $SkipBuild) {
    & "$PSScriptRoot\build.ps1" -Dest $toolDist
    if ($LASTEXITCODE -ne 0) { throw "Tool build failed." }
    & "$PSScriptRoot\skse\build.ps1" -Line both
}

$release = Join-Path $PSScriptRoot "release"
New-Item -ItemType Directory -Force -Path $release | Out-Null

# 1) The tool
$toolZip = Join-Path $release "AnvilLOD-$version.zip"
if (Test-Path $toolZip) { Remove-Item $toolZip }
Copy-Item "$PSScriptRoot\README.md" $toolDist -Force
Compress-Archive -Path "$toolDist\*" -DestinationPath $toolZip
Write-Host "Tool:  $toolZip" -ForegroundColor Green

# 2) The SKSE plugin FOMOD
$stage = Join-Path $env:TEMP "AnvilLOD-fomod-$version"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path "$stage\fomod\images", "$stage\common\SKSE\Plugins" | Out-Null
(Get-Content "$PSScriptRoot\packaging\fomod\info.xml" -Raw).Replace("@VERSION@", $version) | Set-Content "$stage\fomod\info.xml" -Encoding UTF8
Copy-Item "$PSScriptRoot\packaging\fomod\ModuleConfig.xml" "$stage\fomod\"
Copy-Item "$PSScriptRoot\assets\fomod.jpg" "$stage\fomod\images\anvillod.jpg"
Copy-Item "$PSScriptRoot\packaging\AnvilLOD.ini" "$stage\common\SKSE\Plugins\AnvilLOD.ini"
foreach ($line in "1.5.97-1.6.1170", "1.7.x") {
    $src = Join-Path $PSScriptRoot "skse\dist\$line\SKSE\Plugins\AnvilLOD.dll"
    if (-not (Test-Path $src)) { throw "Missing $src - build it with skse\build.ps1 -Line both" }
    New-Item -ItemType Directory -Force -Path "$stage\$line\SKSE\Plugins" | Out-Null
    Copy-Item $src "$stage\$line\SKSE\Plugins\"
    $pdb = [IO.Path]::ChangeExtension($src, ".pdb")
    if (Test-Path $pdb) { Copy-Item $pdb "$stage\$line\SKSE\Plugins\" }
}
$fomodZip = Join-Path $release "AnvilLOD SKSE Plugin-$version.zip"
if (Test-Path $fomodZip) { Remove-Item $fomodZip }
Compress-Archive -Path "$stage\*" -DestinationPath $fomodZip
Remove-Item -Recurse -Force $stage
Write-Host "SKSE:  $fomodZip" -ForegroundColor Green
