<#
.SYNOPSIS
  Copy vendor native runtimes that MSBuild does not carry into a published
  FAAM.UI folder, so the target needs fewer vendor SDK installs.

.DESCRIPTION
  Most vendor natives already reach the publish output via <None ... CopyToOutput>
  items in the adapter csproj files (PI_GCS2_DLL_x64.dll, EziMOTIONPlusE.dll) or
  as <Private>true</Private> managed references (Basler.Pylon.dll,
  ACS.SPiiPlusNET6.dll, ids_peak_*.dll).

  Basler pylon is the gap: Horizon.Camera references only the *managed* wrapper
  (Basler.Pylon.dll). That wrapper P/Invokes the *native* pylon runtime
  (PylonC_v9.dll -> PylonBase_v9.dll -> GenApi/GCBase/...), which lives in the
  pylon install and is never copied. This script stages that native runtime next
  to FAAM.UI.exe, where the Windows loader finds it ahead of any machine-wide
  install.

  Staged from <pylon>\Runtime\x64: all .dll / .cti / .exe EXCEPT the
  pylonDataProcessing* set and the pylonDataProcessingPlugins\ tree (~172 MB of
  vTools image-processing plugins the FAAM camera path never touches).

  IMPORTANT - this does NOT remove the pylon prerequisite entirely. Kernel-mode
  camera drivers cannot be xcopy-deployed:
    * USB3 Vision cameras REQUIRE the Basler USB3 driver from the pylon installer.
    * GigE cameras work over plain sockets, but the pylon performance filter
      driver (and its throughput benefit) also needs the installer.
  Staging the runtime means the app loads and enumerates rather than dying with a
  DllNotFoundException; a rig with real cameras should still install pylon 8.
  See docs/deployment.md section 1.

  Idempotent - safe to re-run against an already-staged folder.

.EXAMPLE
  pwsh deploy/stage-vendor-runtime.ps1 -Target C:\Horizon-deploy\FAAM.UI

.EXAMPLE
  # Fail the build if pylon is missing, instead of just warning:
  pwsh deploy/stage-vendor-runtime.ps1 -Target .\out\FAAM.UI -RequirePylon
#>
[CmdletBinding()]
param(
    # The published application folder to stage into (must contain the .exe).
    [Parameter(Mandatory = $true)]
    [string]$Target,

    # Override pylon SDK root autodetection (the folder holding Runtime\x64).
    [string]$PylonRoot,

    # Treat a missing pylon SDK as an error rather than a warning.
    [switch]$RequirePylon
)

$ErrorActionPreference = 'Stop'

$Target = [System.IO.Path]::GetFullPath($Target)
if (-not (Test-Path -LiteralPath $Target -PathType Container)) {
    throw "Target folder not found: $Target  (publish first - see deploy/publish.ps1)"
}

# Files present in Runtime\x64 that FAAM.UI never loads. The plugins folder is
# excluded wholesale by only copying top-level files.
$excludePrefixes = @('PylonDataProcessing')

function Resolve-PylonRoot {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path -LiteralPath (Join-Path $Explicit 'Runtime\x64')) { return $Explicit }
        throw "-PylonRoot '$Explicit' has no Runtime\x64 folder."
    }

    $candidates = @()

    # PYLON_DEV_DIR points at <pylon>\Development\ - its parent is the SDK root.
    if ($env:PYLON_DEV_DIR) {
        $candidates += (Split-Path -Parent $env:PYLON_DEV_DIR.TrimEnd('\'))
    }
    if ($env:PYLON_ROOT) { $candidates += $env:PYLON_ROOT }

    # Newest-first so a pylon 9 install would win over pylon 8.
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $base) { continue }
        $baslerDir = Join-Path $base 'Basler'
        if (Test-Path -LiteralPath $baslerDir) {
            $candidates += (Get-ChildItem -LiteralPath $baslerDir -Directory -Filter 'pylon*' -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object { $_.FullName })
        }
    }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath (Join-Path $c 'Runtime\x64'))) { return $c }
    }
    return $null
}

Write-Host "Staging vendor runtimes into: $Target" -ForegroundColor Cyan

$pylon = Resolve-PylonRoot -Explicit $PylonRoot

if (-not $pylon) {
    $msg = @'
Basler pylon SDK not found - the native pylon runtime will NOT be bundled.
The published app will still start, but camera connect fails with a
DllNotFoundException unless pylon 8 is installed on the target.
Install pylon 8, or pass -PylonRoot <path>.
'@
    if ($RequirePylon) { throw $msg }
    Write-Warning $msg
    return
}

$src = Join-Path $pylon 'Runtime\x64'

$version = 'unknown'
$versionFile = Join-Path $pylon 'VersionInfo.txt'
if (Test-Path -LiteralPath $versionFile) {
    # VersionInfo.txt is a short key=value / free-text file; first version-looking
    # token is the SDK version.
    $m = [regex]::Match((Get-Content -LiteralPath $versionFile -Raw), '\d+\.\d+\.\d+(\.\d+)?')
    if ($m.Success) { $version = $m.Value }
}

Write-Host "  pylon SDK: $pylon (version $version)"

# Top-level only (-File, no -Recurse) so pylonDataProcessingPlugins\ is skipped.
$files = Get-ChildItem -LiteralPath $src -File |
    Where-Object { $_.Extension -in @('.dll', '.cti', '.exe') } |
    Where-Object {
        $name = $_.Name
        -not ($excludePrefixes | Where-Object { $name.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })
    }

if (-not $files) { throw "No stageable files found under $src" }

$copied = 0
$bytes = 0L
foreach ($f in $files) {
    $dest = Join-Path $Target $f.Name
    # Skip an identical file so re-runs are cheap and don't churn timestamps.
    if (Test-Path -LiteralPath $dest) {
        $existing = Get-Item -LiteralPath $dest
        if ($existing.Length -eq $f.Length -and $existing.LastWriteTimeUtc -eq $f.LastWriteTimeUtc) {
            $bytes += $f.Length
            continue
        }
    }
    Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
    $copied++
    $bytes += $f.Length
}

$mb = [math]::Round($bytes / 1MB, 1)
Write-Host ("  pylon runtime: {0} files, {1} MB ({2} newly copied)" -f $files.Count, $mb, $copied) -ForegroundColor Green

# Manifest so a target machine can tell what vendor bits it actually received.
$manifest = Join-Path $Target 'vendor-runtime.txt'
$lines = @(
    "Vendor native runtimes staged by deploy/stage-vendor-runtime.ps1",
    "Staged on : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  host $env:COMPUTERNAME",
    "",
    "Basler pylon $version  (source: $src)",
    "  Excluded: pylonDataProcessing* and pylonDataProcessingPlugins\ (vTools, unused).",
    "  NOTE: kernel-mode camera drivers are NOT bundled. USB3 Vision cameras need",
    "        the Basler USB3 driver; GigE needs the filter driver for full",
    "        throughput. Install pylon 8 on the target for real camera work.",
    ""
) + ($files | Sort-Object Name | ForEach-Object { "    {0,-52} {1,10:N0} bytes" -f $_.Name, $_.Length })

Set-Content -LiteralPath $manifest -Value $lines -Encoding UTF8
Write-Host "  manifest -> $manifest"
