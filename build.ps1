# Builds AnvilLOD (Release) and copies the app + CLI into one folder you can run from.
#
#   .\build.ps1                                   -> builds into .\dist
#   .\build.ps1 -Dest "E:\Tabula Rasa\tools\AnvilLOD"
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

$stamp = (Get-Item (Join-Path $Dest "AnvilLOD.Plugins.dll")).LastWriteTime
Write-Host "Done. AnvilLOD copied to $Dest (Plugins.dll built $stamp)" -ForegroundColor Green
Write-Host "Run AnvilLOD.App.exe from there (no need to launch it through MO2)."
