# Raises the last number of the AnvilLOD version (VERSION file, used by the tool and the SKSE plugin):
#   .\bump.ps1            0.2.0 -> 0.2.1
#   .\bump.ps1 -Minor     0.2.5 -> 0.3.0
param([switch]$Minor)
$file = Join-Path $PSScriptRoot "VERSION"
$parts = (Get-Content $file -Raw).Trim().Split('.') | ForEach-Object { [int]$_ }
if ($Minor) { $parts[1]++; $parts[2] = 0 } else { $parts[2]++ }
$new = "$($parts[0]).$($parts[1]).$($parts[2])"
Set-Content -Path $file -Value $new -NoNewline
Write-Host "AnvilLOD version: $new" -ForegroundColor Cyan
