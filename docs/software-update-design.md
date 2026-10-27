# FAAM.UI software update over the internet

FAAM.UI stations update themselves from the GitHub Releases of `komcat/Horizon`
(public), using [Velopack](https://velopack.io) 1.2.161. Scope is **update only**:
no rollback UI and no automatic pre-update backup — back up `%APPDATA%\Horizon`
by hand (`deploy/snapshot-appdata.ps1`) when a release changes data formats.

## On a station

```
<install root>\            Setup.exe → %LOCALAPPDATA%\FAAM.UI ; Portable.zip → any folder
  Update.exe               Velopack's updater — runs only while an update installs
  current\                 the app: FAAM.UI.exe, Horizon DLLs, .NET runtime, vendor natives
  packages\                the last downloaded package (the base for the next delta)
```

`current\FAAM.UI.exe` keeps the same path across versions, so the GigE firewall
rule (`allow-firewall.ps1`, rules are per exe path) is added **once** per install.
A copy run from a plain publish folder or Visual Studio is not a Velopack install:
the Version window says so and offers no update.

## Flow

1. **Check** — `HomeView` checks quietly 10 s after the shell starts; a newer release
   turns the Version tile's text accent-coloured ("v0.6.0 available"). The Version
   window checks again when opened (**Check** re-runs it).
2. **Update…** — Engineer level (`AccessPolicy.SoftwareUpdate`), refused while a
   program / deck (`IsRunning`), a Run-page step or an alignment (`ActivityGate`) runs.
   One confirmation (no password), then the download — delta when possible, else full;
   Velopack verifies the checksum. Closing the window cancels it; nothing has changed.
3. **Exit** — the window closes with `UpdateReady`; `ShellWindow.ExitForUpdate` runs the
   normal exit without asking again. `ShutdownWindow` disconnects all hardware, *then*
   calls `AppUpdater.ApplyPendingAtExit`, which starts `Update.exe`. The updater waits
   for FAAM to end (Velopack allows 60 s — hence only after teardown), replaces
   `current\` as a whole and relaunches FAAM.

`%APPDATA%\Horizon` (config, products, users, `history.db`, `oee.db`, logs) is never
touched. Anything written *inside* `current\` is lost on update — nothing may write there.

Auto-apply on start-up is off (`Program.Main`: `SetAutoApplyOnStartup(false)`), so a
downloaded update installs only through the confirmed path above.

## Code

| Piece | Where |
|---|---|
| Velopack hook before WPF starts | `src/FAAM.UI/Program.cs` (`<StartupObject>` in `FAAM.UI.csproj`) |
| Check / download / apply-at-exit | `src/FAAM.UI/AppUpdater.cs` |
| UI: status, Check, Update… | `VersionHistoryWindow` |
| Tile hint + exit hand-off | `HomeView` (`UpdateExitRequested`) → `ShellWindow.ExitForUpdate` |
| Apply after teardown | `ShutdownWindow.RunShutdownAsync` |

`AppUpdater` searches pre-releases too, because 0.x releases are cut with `-PreRelease`.
Unauthenticated GitHub API use is limited to 60 requests / hour per public IP — a
check costs a request or two, so even a floor of stations behind one NAT stays well
inside it.

## Releasing

`deploy/release.ps1` does it as part of the normal release (skip with `-NoUpdatePackage`):

1. `vpk download github` — fetch the previous release's full package (for the delta;
   the first Velopack release has none and ships full only).
2. `vpk pack` from `artifacts\deploy\FAAM.UI` (so run `publish.ps1 -Zip` first) into
   `artifacts\velopack`, version = `<Version>` in `FAAM.UI.csproj`, notes = the release notes.
3. After `gh release create`: `vpk upload github --merge --tag v<n>` adds
   `releases.win.json`, `FAAM.UI-<ver>-full.nupkg` / `-delta.nupkg`, `FAAM.UI-win-Setup.exe`
   and `FAAM.UI-win-Portable.zip` to the same release.

vpk runs pinned through `dotnet tool exec vpk@1.2.161` (.NET 10 SDK, no install).
**Keep the vpk version and the `Velopack` package version in `FAAM.UI.csproj` equal.**
Every release must bump `<Version>` — Velopack only offers a strictly newer version.

Packages are unsigned, so Windows SmartScreen may warn on the first `Setup.exe` run.

## First install on an existing station

Stations running an unzipped publish folder move over once: run the release's
`FAAM.UI-win-Setup.exe` (or unzip `FAAM.UI-win-Portable.zip` into a fixed folder),
then run `allow-firewall.ps1` against the new `current\` folder. `%APPDATA%\Horizon`
carries over unchanged. Every later release arrives through Home › Version › Update.
