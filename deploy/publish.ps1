<#
.SYNOPSIS
  Self-contained-publish the deployable Horizon apps into one staging folder
  ready to copy to a target machine (or to attach to a GitHub release).

.DESCRIPTION
  Publishes FAAM.UI, Horizon.Console and Horizon.ConfigTool for win-x64,
  self-contained (bundles the .NET 8 runtime - no runtime install needed on the
  target). Native vendor DLLs (HALCON / PI / ACS / Basler / Fastech / Thorlabs)
  are only copied when their SDKs are installed on THIS build machine, so run
  this on the dev box where the SDKs live - see docs/deployment.md.

  Folder publish (PublishSingleFile stays off): WPF + native P/Invoke DLLs do not
  play well with single-file packing.

  After each app is published, deploy/stage-vendor-runtime.ps1 copies the native
  Basler pylon runtime alongside any app that carries the managed Basler.Pylon
  wrapper. MSBuild never copies that runtime on its own. Suppress with
  -NoVendorRuntime.

.EXAMPLE
  pwsh deploy/publish.ps1 -Configuration Release -Output C:\Horizon-deploy

.EXAMPLE
  # Just the operator app, zipped for a GitHub release asset:
  pwsh deploy/publish.ps1 -Apps FAAM.UI -Zip -Version 0.1

.EXAMPLE
  # Publish the apps AND bundle the %APPDATA%\Horizon seed in one shot:
  pwsh deploy/publish.ps1 -Snapshot -Output C:\Horizon-deploy

.EXAMPLE
  # Full GitHub release set: app zips + seed + the separate .moon scripts bundle:
  pwsh deploy/publish.ps1 -Zip -Snapshot -CleanHistory -Scripts
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$Output        = (Join-Path $PSScriptRoot '..\artifacts\deploy'),

    # Which apps to publish. Defaults to the FAAM.UI release set; a DLAM release is
    # -Apps DLAM.UI,Horizon.Console,Horizon.ConfigTool.
    [ValidateSet('FAAM.UI', 'DLAM.UI', 'Horizon.Console', 'Horizon.ConfigTool')]
    [string[]]$Apps = @('FAAM.UI', 'Horizon.Console', 'Horizon.ConfigTool'),

    # Skip staging the native Basler pylon runtime into the published folders.
    [switch]$NoVendorRuntime,

    # Zip each published app folder as <App>-v<Version>-<Runtime>.zip.
    [switch]$Zip,
    # Version string used in the zip name (e.g. 0.1). Defaults to the operator app's
    # <Version> - DLAM.UI's when only DLAM.UI is published, else FAAM.UI's.
    [string]$Version,

    # Also snapshot %APPDATA%\Horizon (config + products + history) into the output.
    [switch]$Snapshot,
    # With -Snapshot: drop history.db + each product's dut\ for fresh traceability.
    [switch]$CleanHistory,

    # Also zip every product's .moon scripts folder as Horizon-scripts-v<Version>.zip
    # (deploy/bundle-scripts.ps1) - a station can take a script update on its own.
    [switch]$Scripts
)

$ErrorActionPreference = 'Stop'

# Repo root is the parent of this /deploy folder.
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Output = [System.IO.Path]::GetFullPath($Output)

$projectFor = @{
    'FAAM.UI'            = 'src\FAAM.UI\FAAM.UI.csproj'
    'DLAM.UI'            = 'src\DLAM.UI\DLAM.UI.csproj'
    'Horizon.Console'    = 'src\Horizon.Console\Horizon.Console.csproj'
    'Horizon.ConfigTool' = 'src\Horizon.ConfigTool\Horizon.ConfigTool.csproj'
}

# Default the zip version to whatever the operator app's csproj declares, so the release
# asset name and the assembly version cannot drift apart.
if (($Zip -or $Scripts) -and -not $Version) {
    $versionApp = if ($Apps -contains 'DLAM.UI' -and $Apps -notcontains 'FAAM.UI') { 'DLAM.UI' } else { 'FAAM.UI' }
    $appProj = Join-Path $repo $projectFor[$versionApp]
    $m = [regex]::Match((Get-Content -LiteralPath $appProj -Raw), '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "-Zip needs -Version: no <Version> found in $appProj" }
    $Version = $m.Groups[1].Value
}

Write-Host "Repo:    $repo"
Write-Host "Output:  $Output"
Write-Host "Apps:    $($Apps -join ', ')"
Write-Host "Config:  $Configuration  Runtime: $Runtime  (self-contained)`n"

if (Test-Path $Output) {
    Write-Host "Cleaning existing output folder..."
    Remove-Item $Output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$staged = @()

foreach ($name in $Apps) {
    $projPath = Join-Path $repo $projectFor[$name]
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

    # The native pylon runtime is only relevant to an app that actually carries
    # the managed wrapper, i.e. one that pulled in Horizon.Camera.
    if (-not $NoVendorRuntime -and (Test-Path (Join-Path $dest 'Basler.Pylon.dll'))) {
        & (Join-Path $PSScriptRoot 'stage-vendor-runtime.ps1') -Target $dest
    }

    # A camera-carrying app needs an inbound firewall rule per install path, or the
    # GigE image stream is dropped ("no video"). Ship the script that adds it, plus the
    # station README that says when and how to run it.
    if (Test-Path (Join-Path $dest 'Basler.Pylon.dll')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'allow-firewall.ps1') -Destination $dest -Force
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.txt') -Destination $dest -Force
    }

    $staged += [pscustomobject]@{ Name = $name; Path = $dest }
    Write-Host "    -> $dest`n"
}

Write-Host "Done. Staged apps under: $Output" -ForegroundColor Green

if ($Zip) {
    Write-Host "`n==> Zipping release assets (v$Version)" -ForegroundColor Cyan
    foreach ($app in $staged) {
        $zipPath = Join-Path $Output ("{0}-v{1}-{2}.zip" -f $app.Name, $Version, $Runtime)
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path (Join-Path $app.Path '*') -DestinationPath $zipPath -CompressionLevel Optimal
        $mb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
        Write-Host ("    {0}  ({1} MB)" -f (Split-Path -Leaf $zipPath), $mb) -ForegroundColor Green
    }
}

if ($Snapshot) {
    # Snapshot runs AFTER publish so the Output-folder clean above doesn't wipe the zip.
    Write-Host "`n==> Snapshotting %APPDATA%\Horizon" -ForegroundColor Cyan
    $snap = Join-Path $PSScriptRoot 'snapshot-appdata.ps1'
    $snapArgs = @{ Output = $Output }
    if ($CleanHistory) { $snapArgs.CleanHistory = $true }
    & $snap @snapArgs
}

if ($Scripts) {
    # After the publish too, for the same reason as the snapshot.
    Write-Host "`n==> Bundling product scripts" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'bundle-scripts.ps1') -Output $Output -Version $Version
}

Write-Host "`nNext: copy this folder to the target." -ForegroundColor Green
if ($Snapshot) {
    Write-Host "On the target, restore the data with restore-appdata.ps1, then set clone-specific fields (docs/deployment.md)."
} else {
    Write-Host "Then set up %APPDATA%\Horizon\ per docs/deployment.md (or re-run with -Snapshot to bundle it)."
}
