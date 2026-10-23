<#
.SYNOPSIS
  Snapshot the live %APPDATA%\Horizon data tree (config + products + history) into
  a single zip, ready to ship with the published apps.

.DESCRIPTION
  Captures a deployable "seed" of the FAAM/Horizon runtime data that lives OUTSIDE
  the repo. By default excludes logs\ (rolling, machine-local noise). Optionally
  drops the per-product dut\ and siphog_data\ history and the machine-global
  history.db so the clone starts with clean traceability.

  Run this on the dev/golden box at a known-good moment (nothing mid-edit).

.EXAMPLE
  pwsh deploy/snapshot-appdata.ps1
  pwsh deploy/snapshot-appdata.ps1 -CleanHistory -Output C:\Horizon-deploy
#>
[CmdletBinding()]
param(
    # Where to drop the zip. Defaults to the same staging folder publish.ps1 uses.
    [string]$Output      = (Join-Path $PSScriptRoot '..\artifacts\deploy'),
    # Include rolling Serilog logs (normally useless on a new box).
    [switch]$IncludeLogs,
    # Exclude history.db, oee.db and every product's dut\ + siphog_data\ folders - fresh traceability on the clone.
    [switch]$CleanHistory
)

$ErrorActionPreference = 'Stop'

$src = Join-Path $env:APPDATA 'Horizon'
if (-not (Test-Path $src)) { throw "Source not found: $src" }

$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$zip = Join-Path $Output 'Horizon-appdata.zip'

# Stage into a temp copy so we can exclude folders before zipping.
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("horizon-snap-" + [guid]::NewGuid().ToString('N'))
$dst   = Join-Path $stage 'Horizon'
New-Item -ItemType Directory -Force -Path $dst | Out-Null

Write-Host "Snapshotting: $src" -ForegroundColor Cyan

# robocopy mirrors the tree; /XD skips directories by name at any depth.
$excludeDirs = @()
if (-not $IncludeLogs)  { $excludeDirs += 'logs' }
if ($CleanHistory)      { $excludeDirs += 'dut'; $excludeDirs += 'siphog_data' }

$roboArgs = @($src, $dst, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1')
if ($excludeDirs.Count) { $roboArgs += '/XD'; $roboArgs += $excludeDirs }
robocopy @roboArgs | Out-Null
# robocopy exit codes 0-7 are success; 8+ is a real failure.
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)" }

if ($CleanHistory) {
    # oee.db is this station's uptime / downtime log - never clone it to another station.
    foreach ($db in @('history.db', 'oee.db')) {
        Remove-Item (Join-Path $dst $db) -Force -ErrorAction SilentlyContinue
    }
    Write-Host "  (dropped history.db + oee.db + dut\ + siphog_data\ - clean traceability)" -ForegroundColor DarkGray
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dst '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Wrote $zip  ($size MB)" -ForegroundColor Green

# Ship the restore script alongside the zip so the target doesn't need the repo.
$restore = Join-Path $PSScriptRoot 'restore-appdata.ps1'
if (Test-Path $restore) {
    Copy-Item $restore (Join-Path $Output 'restore-appdata.ps1') -Force
    Write-Host "Bundled restore-appdata.ps1 next to the zip." -ForegroundColor Green
}

Write-Host "On the target: powershell -ExecutionPolicy Bypass -File .\restore-appdata.ps1 -Zip .\Horizon-appdata.zip"
