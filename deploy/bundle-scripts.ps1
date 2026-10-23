<#
.SYNOPSIS
  Zip every product's .moon scripts folder (%APPDATA%\Horizon\products\<name>\scripts)
  into one release asset, Horizon-scripts-v<Version>.zip.

.DESCRIPTION
  The scripts live outside the repo, in each product's data folder, and change far more
  often than the apps. This bundle lets a station take a script update on its own,
  without restoring the whole %APPDATA%\Horizon seed (which would overwrite its config).

  The zip mirrors the %APPDATA%\Horizon layout - products\<name>\scripts\*.moon - so on
  the target it extracts straight over that folder. Same-named scripts are replaced;
  scripts only the target has are left alone. Products with no scripts are skipped.
  Script presets (config\faam_script_presets.json) are NOT included: they are station
  config and ride in the full seed.

  Run on the dev/golden box. All ASCII so it survives a target with another code page.

.EXAMPLE
  pwsh deploy/bundle-scripts.ps1
  pwsh deploy/bundle-scripts.ps1 -Version 0.5 -Output C:\Horizon-deploy

.NOTES
  On the target (Windows PowerShell 5.1), with FAAM.UI closed:
    Expand-Archive .\Horizon-scripts-v<Version>.zip -DestinationPath "$env:APPDATA\Horizon" -Force
#>
[CmdletBinding()]
param(
    # Where to drop the zip. Defaults to the same staging folder publish.ps1 uses.
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\deploy'),
    # Version in the zip name (e.g. 0.5). Defaults to FAAM.UI's <Version>.
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Version) {
    $faamProj = Join-Path $repo 'src\FAAM.UI\FAAM.UI.csproj'
    $m = [regex]::Match((Get-Content -LiteralPath $faamProj -Raw), '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "Pass -Version: no <Version> found in $faamProj" }
    $Version = $m.Groups[1].Value
}

$products = Join-Path $env:APPDATA 'Horizon\products'
if (-not (Test-Path -LiteralPath $products)) { throw "Products folder not found: $products" }

$Output = [System.IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$zip = Join-Path $Output ("Horizon-scripts-v{0}.zip" -f $Version)

# Stage products\<name>\scripts\ only, so the zip carries nothing else from the product.
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("horizon-scripts-" + [guid]::NewGuid().ToString('N'))
$count = 0
try {
    foreach ($product in Get-ChildItem -LiteralPath $products -Directory) {
        $src = Join-Path $product.FullName 'scripts'
        if (-not (Test-Path -LiteralPath $src)) { continue }
        $files = @(Get-ChildItem -LiteralPath $src -File -Recurse)
        if ($files.Count -eq 0) { continue }

        $dst = Join-Path $stage ("products\{0}\scripts" -f $product.Name)
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force
        $count += $files.Count
        Write-Host ("  {0}: {1} file(s)" -f $product.Name, $files.Count)
    }

    if ($count -eq 0) { throw "No scripts found under $products\*\scripts - nothing to bundle." }

    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

$kb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1KB, 1)
Write-Host ("Wrote {0}  ({1} file(s), {2} KB)" -f $zip, $count, $kb) -ForegroundColor Green
Write-Host 'On the target: Expand-Archive .\<zip> -DestinationPath "$env:APPDATA\Horizon" -Force'
