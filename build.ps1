# Builds AnvilLOD (Release) and copies the app + CLI into one folder you can run from.
#
#   .\build.ps1                                   -> builds into .\dist
#   .\build.ps1 -Dest "D:\Modlists\MyList\tools\AnvilLOD"
#
# Build the SKSE plugin first (skse\build.ps1); its DLLs in skse\dist are bundled into the tool's SKSE folder.
#
# Always copy the WHOLE output folder: AnvilLOD.App.exe is only a small launcher,
# the actual program lives in the .dll files next to it.

param(
    [string]$Dest = (Join-Path $PSScriptRoot "dist")
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Building AnvilLOD (Release)..." -ForegroundColor Cyan
dotnet build AnvilLOD.sln -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED - nothing was copied. Send the errors above to Claude." -ForegroundColor Red
    exit $LASTEXITCODE
}

$app = Join-Path $PSScriptRoot "src\AnvilLOD.App\bin\x64\Release\net10.0-windows"
$cli = Join-Path $PSScriptRoot "src\AnvilLOD.Cli\bin\x64\Release\net10.0"

New-Item -ItemType Directory -Force -Path $Dest | Out-Null
Copy-Item "$app\*" $Dest -Recurse -Force
Copy-Item "$cli\*" $Dest -Recurse -Force

# Bundle the SKSE plugin builds, so Generate can put the right AnvilLOD.dll into the LOD output
# (build them first with skse\build.ps1 -Line all; line 1 = SE 1.5.97 - AE 1.6.1170, line 17 = newer, vr = Skyrim VR 1.4.15).
foreach ($line in @("1.5.97-1.6.1170", "1.7.x", "1.4.15-VR")) {
    $dll = Join-Path $PSScriptRoot "skse\dist\$line\SKSE\Plugins\AnvilLOD.dll"
    $to = Join-Path $Dest "SKSE\$line"
    if (Test-Path $dll) {
        New-Item -ItemType Directory -Force -Path $to | Out-Null
        Copy-Item $dll $to -Force
        Write-Host "Bundled SKSE plugin ($line), built $((Get-Item $dll).LastWriteTime)"
    } else {
        $which = switch ($line) { "1.7.x" { "17" } "1.4.15-VR" { "vr" } default { "1" } }
        Write-Host "No SKSE plugin build for $line yet (skse\build.ps1 -Line $which) - Generate can't install that one." -ForegroundColor Yellow
    }
}

$stamp = (Get-Item (Join-Path $Dest "AnvilLOD.Plugins.dll")).LastWriteTime
Write-Host "Done. AnvilLOD copied to $Dest (Plugins.dll built $stamp)" -ForegroundColor Green
Write-Host "Run AnvilLOD.App.exe from there (no need to launch it through MO2)."
