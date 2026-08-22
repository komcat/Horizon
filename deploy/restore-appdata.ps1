<#
.SYNOPSIS
  Restore a Horizon-appdata.zip snapshot into %APPDATA%\Horizon on a target machine.

.DESCRIPTION
  Extracts the seed captured by snapshot-appdata.ps1. If the target already has a
  %APPDATA%\Horizon tree, it is moved aside to Horizon.bak-<timestamp> first (never
  silently overwritten), then the snapshot is extracted clean.

  Run this on the target after copying the deploy folder over. Remember to still set
  the clone-specific fields afterwards (stationId, camera SerialNumber) - see
  docs/deployment.md.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\restore-appdata.ps1 -Zip .\Horizon-appdata.zip

.NOTES
  Runs on stock Windows PowerShell 5.1 - targets usually do not have pwsh
  (PowerShell 7) installed. If pwsh is present, it works there too.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Zip
)

$ErrorActionPreference = 'Stop'

$Zip = (Resolve-Path $Zip).Path
$dst = Join-Path $env:APPDATA 'Horizon'

if (Test-Path $dst) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $bak   = "$dst.bak-$stamp"
    Write-Host "Existing data found - backing up to: $bak" -ForegroundColor Yellow
    Move-Item $dst $bak
}

New-Item -ItemType Directory -Force -Path $dst | Out-Null
Write-Host "Extracting $Zip -> $dst" -ForegroundColor Cyan
Expand-Archive -Path $Zip -DestinationPath $dst -Force

Write-Host "Restored." -ForegroundColor Green
Write-Host "Now set clone-specific fields (docs/deployment.md):"
Write-Host "  - config\app_settings.json : stationId (unique per station)"
Write-Host "  - config\camera-config.json : SerialNumber (this camera's serial)"
Write-Host "  - verify motor_devices / camera IPs on 192.168.0.x"
