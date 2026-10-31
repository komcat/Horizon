<#
.SYNOPSIS
  Tag the current commit and publish a GitHub release for an operator app (FAAM.UI,
  or DLAM.UI with -App DLAM.UI), attaching the published app zips (plus the %APPDATA%
  seed) as the release assets.

.DESCRIPTION
  Assumes the assets already exist - build them first with:

    pwsh deploy/publish.ps1 -Zip -Snapshot -CleanHistory -Scripts

  A release carries the operator app AND the two console tools, because a target
  is brought up bottom-up: Horizon.ConfigTool authors/validates the device config,
  Horizon.Console proves the hardware headlessly, then FAAM.UI runs the process.
  Shipping only the UI would leave a new station with no way to fix its config.

  The two heads release independently from the same repo:

    -App FAAM.UI (default)  tag v<Version>       Velopack channel 'win' (the default)
    -App DLAM.UI            tag dlam-v<Version>  Velopack channel 'dlam'

  An install updates only from the channel it was packed with, so a DLAM release is
  never offered to a FAAM station and the reverse.

  This script only does the git/GitHub side:
    1. sanity-check the working tree, the gh CLI and its auth
    2. resolve the release assets in artifacts\deploy
    3. check the tag version agrees with <Version> in <App>.csproj
    4. pack the <App> software update with Velopack (vpk) into artifacts\velopack,
       as a delta against the previous release's package when there is one
    5. create an annotated tag and push it to origin
    6. gh release create, with docs/release-notes-<tag>.md as the body, and
       upload the assets
    7. vpk upload github --merge: add the update packages to that same release,
       where installed stations find them (Home > Version > Update)

  Requires the GitHub CLI, authenticated once with:  gh auth login
  and the .NET 10 SDK for 'dotnet tool exec' (runs vpk without installing it).

.EXAMPLE
  # See exactly what would happen, change nothing:
  pwsh deploy/release.ps1 -Version 0.1 -DryRun

.EXAMPLE
  # A DLAM.UI release (tag dlam-v0.1, channel dlam), after
  # pwsh deploy/publish.ps1 -Apps DLAM.UI,Horizon.Console,Horizon.ConfigTool -Zip
  pwsh deploy/release.ps1 -App DLAM.UI -Version 0.1 -PreRelease -NoAppData -NoScripts

.EXAMPLE
  # Attach an explicit set instead of the auto-resolved one:
  pwsh deploy/release.ps1 -Version 0.1 -Assets .\artifacts\deploy\FAAM.UI-v0.1.0-win-x64.zip
#>
[CmdletBinding()]
param(
    # Release version without the leading 'v' (e.g. 0.1). The tag becomes v<Version>
    # (dlam-v<Version> for DLAM.UI).
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # The operator app being released.
    [ValidateSet('FAAM.UI', 'DLAM.UI')]
    [string]$App = 'FAAM.UI',

    # Files to attach. Defaults to the app zips for this version in
    # artifacts\deploy, plus Horizon-appdata.zip + restore-appdata.ps1 and the
    # Horizon-scripts-v<Version>.zip scripts bundle if present.
    [Alias('Asset')]
    [string[]]$Assets,

    # Publish with only the app zips - skip the %APPDATA%\Horizon seed even if
    # snapshot-appdata.ps1 left one in artifacts\deploy.
    [switch]$NoAppData,

    # Publish without the separate .moon scripts bundle even if one is present.
    [switch]$NoScripts,

    # Skip the Velopack update packages (Setup.exe, Portable.zip, full/delta .nupkg,
    # releases.<channel>.json) that let installed stations update themselves from
    # this release. See docs/software-update-design.md.
    [switch]$NoUpdatePackage,

    # Do not tag or create anything: add/replace the assets on the EXISTING
    # release for this tag and refresh its body from the notes file. Use this to
    # attach an asset that a release shipped without.
    [switch]$UploadOnly,

    # Markdown file used as the release body. Defaults to docs/release-notes-<tag>.md.
    [string]$NotesFile,

    # Publish as a draft so it can be reviewed on github.com before going live.
    [switch]$Draft,

    # Mark as a pre-release (sensible for a 0.x internal build).
    [switch]$PreRelease,

    # Print the plan and exit without tagging, pushing or creating anything.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Fail($msg) { throw $msg }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Per head: the tag prefix (also version-info.ps1's -TagPrefix in the csproj) and the
# Velopack channel. FAAM.UI keeps vpk's default channel ('win') so stations installed
# before DLAM existed keep updating.
$tagPrefix = if ($App -eq 'DLAM.UI') { 'dlam-v' } else { 'v' }
$channel   = if ($App -eq 'DLAM.UI') { 'dlam' } else { $null }
$tag       = "$tagPrefix$Version"

# The apps publish.ps1 -Zip produces for this head. All three ship in a release: the UI
# runs the process, the ConfigTool authors the target's hardware config, the Console
# proves that config against real devices before the UI is trusted with it.
$releaseApps = @($App, 'Horizon.Console', 'Horizon.ConfigTool')

# The tag is coarse (v0.1) while the assembly version is a full triple (0.1.0),
# and publish.ps1 names the zip after the latter. Accept the exact name first,
# then any zip whose version refines the tag (0.1 -> 0.1.0).
# $suffix is the runtime part of the name; the scripts bundle has none.
function Resolve-AppZip($deployDir, $app, $suffix = '-win-x64') {
    $exact = Join-Path $deployDir "$app-v$Version$suffix.zip"
    if (Test-Path -LiteralPath $exact) { return $exact }

    $candidates = @(Get-ChildItem -LiteralPath $deployDir -Filter "$app-v$Version.*$suffix.zip" -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 1) { return $candidates[0].FullName }
    if ($candidates.Count -gt 1) {
        Fail ("Ambiguous {0} asset for v{1} - pass -Assets explicitly:`n  {2}" -f $app, $Version, (($candidates.Name) -join "`n  "))
    }
    return $null   # missing; the caller reports it
}

$deployDir = Join-Path $repo 'artifacts\deploy'

if (-not $Assets) {
    $resolved = @()
    $missing  = @()
    foreach ($app in $releaseApps) {
        $zip = Resolve-AppZip $deployDir $app
        if ($zip) { $resolved += $zip } else { $missing += $app }
    }
    if ($missing) {
        # Creating a release with an app silently absent is a defect; topping up an
        # existing one with whatever was just rebuilt is the point of -UploadOnly.
        if (-not $UploadOnly) {
            Fail ("No v{0} zip in {1} for: {2}`n  Build them first: pwsh deploy/publish.ps1 -Zip`n  (or pass -Assets to attach a different set)" -f
                  $Version, $deployDir, ($missing -join ', '))
        }
        Write-Host ("  (not rebuilt, leaving as-is on the release: {0})" -f ($missing -join ', ')) -ForegroundColor DarkGray
    }

    # The %APPDATA%\Horizon seed and its restore script travel with the apps when
    # snapshot-appdata.ps1 has produced them - the app zips alone connect to nothing.
    if (-not $NoAppData) {
        foreach ($extra in @('Horizon-appdata.zip', 'restore-appdata.ps1')) {
            $p = Join-Path $deployDir $extra
            if (Test-Path -LiteralPath $p) { $resolved += $p }
            elseif ($UploadOnly) { Write-Host "  (no $extra in $deployDir - leaving as-is on the release)" -ForegroundColor DarkGray }
            else { Write-Host "  (no $extra in $deployDir - releasing without the data seed)" -ForegroundColor Yellow }
        }
    }

    # The .moon scripts bundle (bundle-scripts.ps1) - named by version, like the app zips.
    if (-not $NoScripts) {
        $scriptsZip = Resolve-AppZip $deployDir 'Horizon-scripts' ''
        if ($scriptsZip) { $resolved += $scriptsZip }
        elseif ($UploadOnly) { Write-Host "  (no Horizon-scripts-v$Version zip in $deployDir - leaving as-is on the release)" -ForegroundColor DarkGray }
        else { Write-Host "  (no Horizon-scripts-v$Version zip in $deployDir - releasing without the scripts bundle)" -ForegroundColor Yellow }
    }

    $Assets = $resolved
}
if (-not $NotesFile) {
    $NotesFile = Join-Path $repo "docs\release-notes-$tag.md"
}

# --- preflight ---------------------------------------------------------------

# PATH in an already-open shell predates a fresh winget install, so fall back to
# the standard install locations before giving up.
function Resolve-Gh {
    $cmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @(
            (Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'GitHub CLI\gh.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\gh.exe'))) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    return $null
}

$ghExe = Resolve-Gh
if (-not $ghExe) {
    Fail "GitHub CLI not found. Install it (winget install --id GitHub.cli) and run 'gh auth login'."
}

# gh auth status exits non-zero when not logged in.
& $ghExe auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Fail "gh is not authenticated. Run '`"$ghExe`" auth login' (interactive), then re-run this script."
}

if (-not $Assets) {
    Fail "No release assets resolved.`n  Build them first: pwsh deploy/publish.ps1 -Zip -Snapshot -CleanHistory"
}
foreach ($a in $Assets) {
    if (-not (Test-Path -LiteralPath $a)) {
        Fail "Release asset not found: $a`n  Build it first: pwsh deploy/publish.ps1 -Zip"
    }
}
if (-not (Test-Path -LiteralPath $NotesFile)) {
    Fail "Release notes not found: $NotesFile"
}

# The tag must agree with the version stamped into the assembly, or the binary
# and the release page disagree about what this build is.
$appProj = Join-Path $repo "src\$App\$App.csproj"
$m = [regex]::Match((Get-Content -LiteralPath $appProj -Raw), '<Version>([^<]+)</Version>')
if (-not $m.Success) { Fail "No <Version> element in $appProj" }
$asmVersion = $m.Groups[1].Value
if (-not $asmVersion.StartsWith($Version)) {
    Fail "Version mismatch: tag '$tag' vs <Version>$asmVersion</Version> in $App.csproj. Reconcile them first."
}

# --- Velopack software update ------------------------------------------------
# Installed stations check this repo's releases for releases.<channel>.json and download the
# full / delta .nupkg it lists (Horizon.Station.UI AppUpdater). The vpk CLI version must
# match the Velopack NuGet package the station library references.
$VpkVersion = '1.2.161'
$repoUrl    = 'https://github.com/komcat/Horizon'
$vpkDir     = Join-Path $repo 'artifacts\velopack'
$appDir     = Join-Path $deployDir $App
$makeUpdate = -not $NoUpdatePackage -and -not $UploadOnly
$channelArgs = if ($channel) { @('--channel', $channel) } else { @() }

# Runs the pinned vpk without installing it; returns its exit code. Output goes to the
# host so it is not mixed into the return value.
function Invoke-Vpk([string[]]$VpkArgs) {
    & dotnet tool exec "vpk@$VpkVersion" --yes '--' @VpkArgs | Out-Host
    return $LASTEXITCODE
}

if ($makeUpdate -and -not (Test-Path -LiteralPath (Join-Path $appDir "$App.exe"))) {
    Fail "No published $App folder at $appDir to pack the update from.`n  Build it first: pwsh deploy/publish.ps1 -Zip  (or pass -NoUpdatePackage)"
}

# Packs artifacts\deploy\<App> into artifacts\velopack. Runs before anything is tagged,
# so a packing failure leaves no half-made release behind.
function New-UpdatePackage {
    if (Test-Path -LiteralPath $vpkDir) { Remove-Item -LiteralPath $vpkDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $vpkDir | Out-Null
    $token = (& $ghExe auth token).Trim()

    # The previous release's full package (same channel) lets vpk build a small delta.
    # The first Velopack release of a channel has none - it then ships the full package only.
    Write-Host "==> Fetching the previous update package (for the delta)" -ForegroundColor Cyan
    if ((Invoke-Vpk (@('download', 'github', '--repoUrl', $repoUrl, '--token', $token, '--pre', '--outputDir', $vpkDir) + $channelArgs)) -ne 0) {
        Write-Host "  (no previous update package - this release carries the full package only)" -ForegroundColor Yellow
    }

    Write-Host "==> Packing the $App update (Velopack $asmVersion$(if ($channel) { ", channel $channel" }))" -ForegroundColor Cyan
    $packArgs = @('pack',
        '--packId', $App, '--packVersion', $asmVersion, '--packDir', $appDir,
        '--mainExe', "$App.exe", '--packTitle', $App, '--packAuthors', 'Fabrinet',
        '--runtime', 'win-x64',
        '--icon', (Join-Path $repo 'assets\appicon.ico'), '--releaseNotes', $NotesFile,
        '--outputDir', $vpkDir) + $channelArgs
    if ((Invoke-Vpk $packArgs) -ne 0) { Fail "vpk pack failed" }
}

# Adds the packed update to the release gh just created (same tag).
function Publish-UpdatePackage {
    Write-Host "==> Uploading the $App update packages to $tag" -ForegroundColor Cyan
    $token = (& $ghExe auth token).Trim()
    $upArgs = @('upload', 'github', '--outputDir', $vpkDir, '--repoUrl', $repoUrl, '--token', $token,
                '--merge', '--tag', $tag, '--releaseName', "$App $tag") + $channelArgs
    if (-not $Draft)   { $upArgs += '--publish' }
    if ($PreRelease)   { $upArgs += '--pre' }
    if ((Invoke-Vpk $upArgs) -ne 0) {
        Fail "vpk upload failed - the release exists without its update packages. Re-run the upload:`n  dotnet tool exec vpk@$VpkVersion --yes -- upload github --outputDir `"$vpkDir`" --repoUrl $repoUrl --token <gh auth token> --merge --tag $tag $channelArgs"
    }
}

Push-Location $repo
try {
    if ($UploadOnly) {
        & $ghExe release view $tag 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Fail "No release $tag on GitHub to upload to. Drop -UploadOnly to create it."
        }

        Write-Host ""
        Write-Host "  tag        : $tag (existing release)" -ForegroundColor Cyan
        foreach ($a in $Assets) {
            Write-Host ("  upload     : {0} ({1} MB)" -f (Split-Path -Leaf $a),
                        [math]::Round((Get-Item -LiteralPath $a).Length / 1MB, 1))
        }
        Write-Host "  notes      : $NotesFile (body refreshed)"
        Write-Host ""

        if ($DryRun) {
            Write-Host "DryRun - nothing was uploaded." -ForegroundColor Yellow
            return
        }

        # --clobber so re-running after a rebuild replaces the asset instead of failing.
        Write-Host "==> Uploading assets to $tag" -ForegroundColor Cyan
        $upArgs = @('release', 'upload', $tag) + $Assets + @('--clobber')
        & $ghExe @upArgs
        if ($LASTEXITCODE -ne 0) { Fail "gh release upload failed" }

        Write-Host "==> Refreshing release notes" -ForegroundColor Cyan
        & $ghExe release edit $tag --notes-file $NotesFile
        if ($LASTEXITCODE -ne 0) { Fail "gh release edit failed" }

        Write-Host "`nRelease $tag updated." -ForegroundColor Green
        & $ghExe release view $tag --web
        return
    }

    $dirty = git status --porcelain
    if ($dirty) {
        Fail "Working tree is not clean - commit or stash first:`n$dirty"
    }

    # A tag can already exist because an earlier run tagged and pushed but then
    # failed at release creation. Resuming is fine as long as no release exists
    # for it yet; a tag with a release is a genuine version collision.
    $tagExists = [bool](git tag --list $tag)
    if ($tagExists) {
        & $ghExe release view $tag 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Fail "Release $tag already exists on GitHub. Pick a new version, or delete it with 'gh release delete $tag'."
        }
        Write-Host "Tag $tag already exists with no release - resuming at release creation." -ForegroundColor Yellow
    }

    $sha    = (git rev-parse --short HEAD).Trim()
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    $assetLines = @($Assets | ForEach-Object {
        "{0} ({1} MB)" -f (Split-Path -Leaf $_), [math]::Round((Get-Item -LiteralPath $_).Length / 1MB, 1)
    })

    # A tag whose commit is absent from origin gives a release page pointing at
    # source GitHub cannot show, so the branch has to go up first.
    git fetch origin $branch --quiet 2>&1 | Out-Null
    $unpushed = @(git rev-list "origin/$branch..HEAD" 2>$null).Count

    Write-Host ""
    Write-Host "  tag        : $tag" -ForegroundColor Cyan
    Write-Host "  commit     : $sha (on $branch)"
    Write-Host "  unpushed   : $unpushed commit(s)$(if ($unpushed) { ' - will push ' + $branch + ' first' })"
    Write-Host "  assets     : $($assetLines[0])"
    foreach ($line in $assetLines | Select-Object -Skip 1) {
        Write-Host "               $line"
    }
    Write-Host "  notes      : $NotesFile"
    Write-Host "  update     : $(if ($makeUpdate) { "Velopack $App $asmVersion, channel $(if ($channel) { $channel } else { 'win (default)' }), from $appDir (Setup.exe, Portable.zip, full + delta .nupkg)" } else { 'skipped (-NoUpdatePackage)' })"
    Write-Host "  draft      : $([bool]$Draft)   prerelease: $([bool]$PreRelease)"
    Write-Host ""

    if ($DryRun) {
        Write-Host "DryRun - nothing was tagged, pushed or created." -ForegroundColor Yellow
        return
    }

    if ($makeUpdate) { New-UpdatePackage }

    if ($unpushed -gt 0) {
        Write-Host "==> Pushing $branch to origin ($unpushed commit(s))" -ForegroundColor Cyan
        git push origin $branch
        if ($LASTEXITCODE -ne 0) { Fail "git push origin $branch failed" }
    }

    if (-not $tagExists) {
        Write-Host "==> Tagging $tag" -ForegroundColor Cyan
        git tag -a $tag -m "$App $tag"
        if ($LASTEXITCODE -ne 0) { Fail "git tag failed" }
    }

    Write-Host "==> Pushing tag to origin" -ForegroundColor Cyan
    git push origin $tag
    if ($LASTEXITCODE -ne 0) {
        # Leave the local tag in place so the user can retry the push.
        Fail "git push failed - the local tag $tag was kept, re-run after fixing the remote."
    }

    # No --target: the tag is already on the remote, so GitHub resolves the commit
    # from it. Passing a short SHA there is rejected as an invalid target_commitish.
    Write-Host "==> Creating GitHub release" -ForegroundColor Cyan
    $ghArgs = @('release', 'create', $tag) + $Assets + @(
        '--title', "$App $tag",
        '--notes-file', $NotesFile
    )
    if ($Draft)      { $ghArgs += '--draft' }
    if ($PreRelease) { $ghArgs += '--prerelease' }

    & $ghExe @ghArgs
    if ($LASTEXITCODE -ne 0) { Fail "gh release create failed" }

    if ($makeUpdate) { Publish-UpdatePackage }

    Write-Host "`nRelease $tag published." -ForegroundColor Green
    & $ghExe release view $tag --web
}
finally {
    Pop-Location
}
