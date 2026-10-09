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

$version = (Get-Content (Join-Path $PSScriptRoot "VERSION") -Raw).Trim()
if (-not $version) { throw "The VERSION file is empty" }
Write-Host "AnvilLOD $version" -ForegroundColor Cyan

$toolDist = Join-Path $PSScriptRoot "dist\tool"
if (-not $SkipBuild) {
    # SKSE first: the tool build bundles both DLLs (tool\SKSE\<line>) so Generate can install the right one.
    & "$PSScriptRoot\skse\build.ps1" -Line both
    & "$PSScriptRoot\build.ps1" -Dest $toolDist
    if ($LASTEXITCODE -ne 0) { throw "Tool build failed." }
}

# Zips with forward-slash entry names (Windows PowerShell's Compress-Archive writes backslashes, which some
# extractors and mod managers turn into flat file names).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function New-Zip([string]$folder, [string]$zipPath) {
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $root = (Resolve-Path $folder).Path.TrimEnd('\') + '\'
        Get-ChildItem $folder -Recurse -File | ForEach-Object {
            $name = $_.FullName.Substring($root.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
}

$release = Join-Path $PSScriptRoot "release"
New-Item -ItemType Directory -Force -Path $release | Out-Null

# 1) The tool
$toolZip = Join-Path $release "AnvilLOD-$version.zip"
Copy-Item "$PSScriptRoot\README.md" $toolDist -Force
Copy-Item "$PSScriptRoot\LICENSE.txt" $toolDist -Force
New-Zip $toolDist $toolZip
Write-Host "Tool:  $toolZip" -ForegroundColor Green

# 2) The SKSE plugin FOMOD
$stage = Join-Path $env:TEMP "AnvilLOD-fomod-$version"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path "$stage\fomod\images", "$stage\common\SKSE\Plugins" | Out-Null
(Get-Content "$PSScriptRoot\packaging\fomod\info.xml" -Raw).Replace("@VERSION@", $version) | Set-Content "$stage\fomod\info.xml" -Encoding UTF8
Copy-Item "$PSScriptRoot\packaging\fomod\ModuleConfig.xml" "$stage\fomod\"
Copy-Item "$PSScriptRoot\assets\fomod.jpg" "$stage\fomod\images\anvillod.jpg"
Copy-Item "$PSScriptRoot\packaging\AnvilLOD.ini" "$stage\common\SKSE\Plugins\AnvilLOD.ini"
Copy-Item "$PSScriptRoot\LICENSE.txt" "$stage\LICENSE.txt"
foreach ($line in "1.5.97-1.6.1170", "1.7.x") {
    $src = Join-Path $PSScriptRoot "skse\dist\$line\SKSE\Plugins\AnvilLOD.dll"
    if (-not (Test-Path $src)) { throw "Missing $src - build it with skse\build.ps1 -Line both" }
    New-Item -ItemType Directory -Force -Path "$stage\$line\SKSE\Plugins" | Out-Null
    Copy-Item $src "$stage\$line\SKSE\Plugins\"
    $pdb = [IO.Path]::ChangeExtension($src, ".pdb")
    if (Test-Path $pdb) { Copy-Item $pdb "$stage\$line\SKSE\Plugins\" }
}
$fomodZip = Join-Path $release "AnvilLOD SKSE Plugin-$version.zip"
New-Zip $stage $fomodZip
Remove-Item -Recurse -Force $stage
Write-Host "SKSE:  $fomodZip" -ForegroundColor Green
