<#
.SYNOPSIS
  Tag the current commit and publish a GitHub release for FAAM.UI, attaching the
  published zip as the release asset.

.DESCRIPTION
  Assumes the asset already exists - build it first with:

    pwsh deploy/publish.ps1 -Apps FAAM.UI -Zip

  This script only does the git/GitHub side:
    1. sanity-check the working tree, the gh CLI and its auth
    2. check the tag version agrees with <Version> in FAAM.UI.csproj
    3. create an annotated tag and push it to origin
    4. gh release create, with docs/release-notes-v<n>.md as the body, and
       upload the zip

  Requires the GitHub CLI, authenticated once with:  gh auth login

.EXAMPLE
  # See exactly what would happen, change nothing:
  pwsh deploy/release.ps1 -Version 0.1 -DryRun

.EXAMPLE
  pwsh deploy/release.ps1 -Version 0.1 -Asset .\artifacts\deploy\FAAM.UI-v0.1-win-x64.zip
#>
[CmdletBinding()]
param(
    # Release version without the leading 'v' (e.g. 0.1). The tag becomes v<Version>.
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # The zip to attach. Defaults to artifacts\deploy\FAAM.UI-v<Version>-win-x64.zip.
    [string]$Asset,

    # Markdown file used as the release body. Defaults to docs/release-notes-v<Version>.md.
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
$tag  = "v$Version"

if (-not $Asset) {
    # The tag is coarse (v0.1) while the assembly version is a full triple
    # (0.1.0), and publish.ps1 names the zip after the latter. Accept the exact
    # name first, then any zip whose version refines the tag (0.1 -> 0.1.0).
    $deployDir = Join-Path $repo 'artifacts\deploy'
    $exact = Join-Path $deployDir "FAAM.UI-v$Version-win-x64.zip"
    if (Test-Path -LiteralPath $exact) {
        $Asset = $exact
    }
    else {
        $candidates = @(Get-ChildItem -LiteralPath $deployDir -Filter "FAAM.UI-v$Version.*-win-x64.zip" -ErrorAction SilentlyContinue)
        if ($candidates.Count -eq 1) {
            $Asset = $candidates[0].FullName
        }
        elseif ($candidates.Count -gt 1) {
            Fail ("Ambiguous release asset for v{0} - pass -Asset explicitly:`n  {1}" -f $Version, (($candidates.Name) -join "`n  "))
        }
        else {
            $Asset = $exact   # let the not-found check below report it
        }
    }
}
if (-not $NotesFile) {
    $NotesFile = Join-Path $repo "docs\release-notes-v$Version.md"
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

if (-not (Test-Path -LiteralPath $Asset)) {
    Fail "Release asset not found: $Asset`n  Build it first: pwsh deploy/publish.ps1 -Apps FAAM.UI -Zip"
}
if (-not (Test-Path -LiteralPath $NotesFile)) {
    Fail "Release notes not found: $NotesFile"
}

# The tag must agree with the version stamped into the assembly, or the binary
# and the release page disagree about what this build is.
$faamProj = Join-Path $repo 'src\FAAM.UI\FAAM.UI.csproj'
$m = [regex]::Match((Get-Content -LiteralPath $faamProj -Raw), '<Version>([^<]+)</Version>')
if (-not $m.Success) { Fail "No <Version> element in $faamProj" }
$asmVersion = $m.Groups[1].Value
if (-not $asmVersion.StartsWith($Version)) {
    Fail "Version mismatch: tag '$tag' vs <Version>$asmVersion</Version> in FAAM.UI.csproj. Reconcile them first."
}

Push-Location $repo
try {
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
    $assetMb = [math]::Round((Get-Item -LiteralPath $Asset).Length / 1MB, 1)

    # A tag whose commit is absent from origin gives a release page pointing at
    # source GitHub cannot show, so the branch has to go up first.
    git fetch origin $branch --quiet 2>&1 | Out-Null
    $unpushed = @(git rev-list "origin/$branch..HEAD" 2>$null).Count

    Write-Host ""
    Write-Host "  tag        : $tag" -ForegroundColor Cyan
    Write-Host "  commit     : $sha (on $branch)"
    Write-Host "  unpushed   : $unpushed commit(s)$(if ($unpushed) { ' - will push ' + $branch + ' first' })"
    Write-Host "  asset      : $Asset ($assetMb MB)"
    Write-Host "  notes      : $NotesFile"
    Write-Host "  draft      : $([bool]$Draft)   prerelease: $([bool]$PreRelease)"
    Write-Host ""

    if ($DryRun) {
        Write-Host "DryRun - nothing was tagged, pushed or created." -ForegroundColor Yellow
        return
    }

    if ($unpushed -gt 0) {
        Write-Host "==> Pushing $branch to origin ($unpushed commit(s))" -ForegroundColor Cyan
        git push origin $branch
        if ($LASTEXITCODE -ne 0) { Fail "git push origin $branch failed" }
    }

    if (-not $tagExists) {
        Write-Host "==> Tagging $tag" -ForegroundColor Cyan
        git tag -a $tag -m "FAAM.UI $tag"
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
    $ghArgs = @(
        'release', 'create', $tag, $Asset,
        '--title', "FAAM.UI $tag",
        '--notes-file', $NotesFile
    )
    if ($Draft)      { $ghArgs += '--draft' }
    if ($PreRelease) { $ghArgs += '--prerelease' }

    & $ghExe @ghArgs
    if ($LASTEXITCODE -ne 0) { Fail "gh release create failed" }

    Write-Host "`nRelease $tag published." -ForegroundColor Green
    & $ghExe release view $tag --web
}
finally {
    Pop-Location
}
