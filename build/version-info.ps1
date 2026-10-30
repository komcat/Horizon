<#
.SYNOPSIS
  Writes a station head's build/version identity (current commit + release history) as JSON,
  embedded into the exe by the head's version-info target (FAAM.UI.csproj FaamVersionInfo,
  DLAM.UI.csproj DlamVersionInfo).

.DESCRIPTION
  Runs before every build (Windows PowerShell 5.1 compatible). Stations get a published
  zip with no git, so the history is captured here, at build time. Releases are the
  annotated <TagPrefix>* tags the head's release script creates (FAAM: v*, DLAM: dlam-v*),
  so each head lists only its own releases. The file is only rewritten when its
  content changes, so an unchanged tree keeps the build incremental. Never fails the
  build: on any error (no git, not a repo) it leaves the previous file / writes nothing.
#>
param(
    [Parameter(Mandatory = $true)][string]$RepoDir,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [string]$Version = '',
    [string]$TagPrefix = 'v'
)

$ErrorActionPreference = 'Stop'
try {
    function Invoke-RepoGit([string[]]$GitArgs) {
        $out = & git.exe -C $RepoDir @GitArgs 2>$null
        if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed" }
        return $out
    }

    $commit     = (Invoke-RepoGit @('rev-parse', 'HEAD')) | Select-Object -First 1
    $short      = (Invoke-RepoGit @('rev-parse', '--short', 'HEAD')) | Select-Object -First 1
    $branch     = (Invoke-RepoGit @('rev-parse', '--abbrev-ref', 'HEAD')) | Select-Object -First 1
    $describe   = (Invoke-RepoGit @('describe', '--tags', '--match', "$TagPrefix*", '--always', '--dirty')) | Select-Object -First 1
    $commitDate = (Invoke-RepoGit @('log', '-1', '--format=%cI', 'HEAD')) | Select-Object -First 1
    $subject    = (Invoke-RepoGit @('log', '-1', '--format=%s', 'HEAD')) | Select-Object -First 1
    $dirty      = [bool]((Invoke-RepoGit @('status', '--porcelain', '--untracked-files=no')) | Select-Object -First 1)

    # Releases: <TagPrefix>* tags, newest first - tag, date (of the tagged commit), short id, tag message.
    $releases = @()
    $tagLines = Invoke-RepoGit @('for-each-ref', '--sort=-creatordate', '--format=%(refname:short)|%(objectname:short)|%(*objectname:short)|%(creatordate:iso-strict)|%(contents:subject)', "refs/tags/$TagPrefix*")
    foreach ($line in @($tagLines)) {
        if (-not $line) { continue }
        $p = $line.Split('|', 5)
        $target = if ($p[2]) { $p[2] } else { $p[1] }   # annotated tag -> peeled commit
        $releases += [ordered]@{ tag = $p[0]; commit = $target; date = $p[3]; title = $p[4] }
    }

    $sinceRelease = 0
    if ($releases.Count -gt 0) {
        $sinceRelease = [int]((Invoke-RepoGit @('rev-list', '--count', "$($releases[0].tag)..HEAD")) | Select-Object -First 1)
    }

    $info = [ordered]@{
        version        = $Version
        describe       = $describe
        commit         = $commit
        shortCommit    = $short
        branch         = $branch
        commitDate     = $commitDate
        commitSubject  = $subject
        dirty          = $dirty
        commitsSinceRelease = $sinceRelease
        releases       = $releases
    }
    $json = $info | ConvertTo-Json -Depth 4

    $dir = Split-Path -Parent $OutFile
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $existing = if (Test-Path $OutFile) { [IO.File]::ReadAllText($OutFile) } else { '' }
    if ($existing -ne $json) {
        [IO.File]::WriteAllText($OutFile, $json, (New-Object Text.UTF8Encoding $false))
    }
}
catch {
    Write-Host "version-info: skipped ($($_.Exception.Message))"
}
exit 0
