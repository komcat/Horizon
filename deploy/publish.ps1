<#
.SYNOPSIS
  Self-contained-publish the three deployable Horizon apps into one staging
  folder ready to copy to a target machine.

.DESCRIPTION
  Publishes FAAM.UI, Horizon.Console and Horizon.ConfigTool for win-x64,
  self-contained (bundles the .NET 8 runtime - no runtime install needed on the
  target). Native vendor DLLs (HALCON / PI / ACS / Basler / Fastech / Thorlabs)
  are only copied when their SDKs are installed on THIS build machine, so run
  this on the dev box where the SDKs live - see docs/deployment.md.

  Folder publish (PublishSingleFile stays off): WPF + native P/Invoke DLLs do not
  play well with single-file packing.

.EXAMPLE
  pwsh deploy/publish.ps1 -Configuration Release -Output C:\Horizon-deploy

.EXAMPLE
  # Publish the apps AND bundle the %APPDATA%\Horizon seed in one shot:
  pwsh deploy/publish.ps1 -Snapshot -Output C:\Horizon-deploy
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$Output        = (Join-Path $PSScriptRoot '..\artifacts\deploy'),
    # Also snapshot %APPDATA%\Horizon (config + products + history) into the output.
    [switch]$Snapshot,
    # With -Snapshot: drop history.db + each product's dut\ for fresh traceability.
    [switch]$CleanHistory
)

$ErrorActionPreference = 'Stop'

# Repo root is the parent of this /deploy folder.
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Output = [System.IO.Path]::GetFullPath($Output)

$projects = @(
    'src\FAAM.UI\FAAM.UI.csproj',
    'src\Horizon.Console\Horizon.Console.csproj',
    'src\Horizon.ConfigTool\Horizon.ConfigTool.csproj'
)

Write-Host "Repo:    $repo"
Write-Host "Output:  $Output"
Write-Host "Config:  $Configuration  Runtime: $Runtime  (self-contained)`n"

if (Test-Path $Output) {
    Write-Host "Cleaning existing output folder..."
    Remove-Item $Output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Output | Out-Null

foreach ($proj in $projects) {
    $projPath = Join-Path $repo $proj
    $name     = [System.IO.Path]::GetFileNameWithoutExtension($projPath)
    $dest     = Join-Path $Output $name

    Write-Host "==> Publishing $name" -ForegroundColor Cyan
    dotnet publish $projPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Platform=x64 `
        -o $dest

    if ($LASTEXITCODE -ne 0) {
        throw "publish failed for $name (exit $LASTEXITCODE)"
    }
    Write-Host "    -> $dest`n"
}

Write-Host "Done. Staged apps under: $Output" -ForegroundColor Green

if ($Snapshot) {
    # Snapshot runs AFTER publish so the Output-folder clean above doesn't wipe the zip.
    Write-Host "`n==> Snapshotting %APPDATA%\Horizon" -ForegroundColor Cyan
    $snap = Join-Path $PSScriptRoot 'snapshot-appdata.ps1'
    $snapArgs = @{ Output = $Output }
    if ($CleanHistory) { $snapArgs.CleanHistory = $true }
    & $snap @snapArgs
}

Write-Host "`nNext: copy this folder to the target." -ForegroundColor Green
if ($Snapshot) {
    Write-Host "On the target, restore the data with restore-appdata.ps1, then set clone-specific fields (docs/deployment.md)."
} else {
    Write-Host "Then set up %APPDATA%\Horizon\ per docs/deployment.md (or re-run with -Snapshot to bundle it)."
}
