<#
.SYNOPSIS
  Allow the Horizon / FAAM executables through Windows Firewall (GigE camera streams).

.DESCRIPTION
  A GigE camera's control channel works without a firewall rule, but its image
  stream (inbound UDP) is dropped for any .exe that has no Allow rule. Rules are
  per executable PATH, so every new build folder needs its own rule - otherwise
  the camera connects, grabbing "starts", and 0 frames arrive.

  This script finds the Horizon executables under -Path and, for each one:
    - removes any Block rule for that exact path (Windows creates one when the
      first-run "Allow access?" prompt is cancelled), and
    - replaces this script's own previous rule with a fresh inbound Allow rule
      (all profiles, any protocol), in the rule group "Horizon".

  Safe to re-run. Must run as Administrator; it relaunches itself elevated if not.

.PARAMETER Path
  Folder holding the build (searched recursively). Default: this script's folder.

.PARAMETER Remove
  Remove the Allow rules this script created for executables under -Path instead.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\allow-firewall.ps1 -Path "C:\FAAM\v0.2"

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\allow-firewall.ps1 -Path "C:\FAAM\v0.2" -Remove

.NOTES
  Runs on stock Windows PowerShell 5.1 - targets usually do not have pwsh
  (PowerShell 7) installed. If pwsh is present, it works there too.
#>
[CmdletBinding()]
param(
    [string]$Path = $PSScriptRoot,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$ExeNames  = @('FAAM.UI.exe', 'Horizon.UI.exe', 'Horizon.App.exe', 'Horizon.Console.exe', 'Horizon.Workflow.Console.exe')
$RuleGroup = 'Horizon'

# --- Elevate if needed (firewall rules need admin) ---
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Not running as Administrator - relaunching elevated..." -ForegroundColor Yellow
    # -NoExit keeps the elevated window open so the result can be read.
    $argList = @('-NoExit', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Path', "`"$Path`"")
    if ($Remove) { $argList += '-Remove' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    return
}

$Path = (Resolve-Path $Path).Path
$exes = @(Get-ChildItem -Path $Path -Recurse -File -Include $ExeNames -ErrorAction SilentlyContinue)
if ($exes.Count -eq 0) {
    Write-Host "No Horizon executables found under: $Path" -ForegroundColor Red
    Write-Host "Pass the build folder, e.g. -Path `"C:\FAAM\v0.2`""
    exit 1
}

foreach ($exe in $exes) {
    $program = $exe.FullName
    Write-Host ""
    Write-Host $program -ForegroundColor Cyan

    # Every rule currently bound to this exact executable path.
    $rules = @(Get-NetFirewallApplicationFilter -Program $program -ErrorAction SilentlyContinue |
               Get-NetFirewallRule -ErrorAction SilentlyContinue)

    # Our own earlier rules for this path (re-run / -Remove).
    foreach ($r in $rules | Where-Object { $_.Group -eq $RuleGroup }) {
        Remove-NetFirewallRule -Name $r.Name
        Write-Host "  removed previous rule: $($r.DisplayName)" -ForegroundColor DarkGray
    }

    if ($Remove) { continue }

    # Block rules (e.g. from a cancelled first-run prompt) win over Allow - drop them.
    foreach ($r in $rules | Where-Object { $_.Action -eq 'Block' -and $_.Group -ne $RuleGroup }) {
        Remove-NetFirewallRule -Name $r.Name
        Write-Host "  removed BLOCK rule: $($r.DisplayName) ($($r.Direction))" -ForegroundColor Yellow
    }

    $display = "Horizon - $($exe.BaseName) ($($exe.Directory.Name))"
    New-NetFirewallRule -DisplayName $display -Group $RuleGroup `
        -Direction Inbound -Action Allow -Program $program -Profile Any `
        -Description "GigE camera stream + device traffic for $program" | Out-Null
    Write-Host "  allowed (inbound, all profiles): $display" -ForegroundColor Green
}

Write-Host ""
if ($Remove) {
    Write-Host "Done - Horizon rules removed for $($exes.Count) executable(s)." -ForegroundColor Green
} else {
    Write-Host "Done - $($exes.Count) executable(s) allowed. Restart FAAM for the camera stream to reconnect." -ForegroundColor Green
}
