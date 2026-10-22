# Horizon / FAAM deployment

Scripts to package FAAM.UI, Horizon.Console and Horizon.ConfigTool and stand them
up on a new machine as a **clone of an existing rig**. Full reference:
`../docs/deployment.md`.

```
deploy/
  build-deploy.bat         DEV box  - double-click: publish.ps1 -Snapshot -CleanHistory -> C:\Horizon-deploy
  publish.ps1              DEV box  - self-contained publish the 3 apps (+ -Snapshot to bundle data)
  stage-vendor-runtime.ps1 DEV box  - copy the native Basler pylon runtime into a published folder
                                      (called automatically by publish.ps1)
  release.ps1              DEV box  - tag + publish a GitHub release with the zipped app
  snapshot-appdata.ps1     DEV box  - zip %APPDATA%\Horizon (config + products + history)
  restore-appdata.ps1      TARGET   - unzip the seed into %APPDATA%\Horizon
```

Run the **DEV box** scripts with `pwsh` (PowerShell 7+). `restore-appdata.ps1` runs
on the **target**, which normally has only stock Windows PowerShell 5.1 — invoke it
with `powershell`, not `pwsh`. All the scripts are ASCII-only so they survive copy
to a target with a different default code page.

---

## On the DEV / golden box

Build the apps and bundle the runtime data in one command:

```powershell
pwsh deploy/publish.ps1 -Snapshot -Output C:\Horizon-deploy
```

Add `-CleanHistory` to drop `history.db` + each product's `dut\` so the clone
starts with fresh traceability:

```powershell
pwsh deploy/publish.ps1 -Snapshot -CleanHistory -Output C:\Horizon-deploy
```

Result - the whole package to copy:

```
C:\Horizon-deploy\
  FAAM.UI\             self-contained app (own .NET runtime + vendor DLLs)
  Horizon.Console\
  Horizon.ConfigTool\
  Horizon-appdata.zip  config + products + history
  restore-appdata.ps1  travels with the zip
```

> Build on the box where the vendor SDKs are installed (Basler / PI / ACS / etc.).
> The hardware adapters pick up their native DLLs only when the SDKs are present,
> so a build on a bare box would ship without them. `Horizon.Camera` hard-references
> `Basler.Pylon.dll` with no `Exists(...)` guard, so a box without pylon 8 cannot
> even build the solution.

`publish.ps1` also stages the **native Basler pylon runtime** (~26 MB) next to any
published app that carries the managed `Basler.Pylon.dll` — MSBuild never copies it
on its own, so without this step the camera dies with a `DllNotFoundException`. The
staged file list is written to `vendor-runtime.txt` in the app folder. Suppress with
`-NoVendorRuntime`; stage into an existing folder with `stage-vendor-runtime.ps1
-Target <folder>`.

Equivalent two-step form (same result):

```powershell
pwsh deploy/publish.ps1          -Output C:\Horizon-deploy
pwsh deploy/snapshot-appdata.ps1 -Output C:\Horizon-deploy
```

---

## On the TARGET box

**1. Install vendor drivers** (only for hardware this rig uses):

| Concern | Install |
|---|---|
| Vision  | HALCON 24.05 runtime + license file (x64) |
| Motion  | PI Software Suite (GCS2), ACS SPiiPlus Runtime Kit |
| Camera  | Basler Pylon 8 (and/or IDS Peak / uEye) |
| IO / Instruments | Fastech Ezi-MOTION, NI-VISA, Thorlabs Kinesis as wired |

.NET is bundled (self-contained), so no .NET install is needed.
Set the NIC to a static address on the controller subnet (this rig: `192.168.0.x`).

**2. Copy** `C:\Horizon-deploy\` to the target (e.g. `C:\Horizon\`).

**3. Restore the data** (existing `%APPDATA%\Horizon` is moved to
`Horizon.bak-<timestamp>` first, never overwritten):

```powershell
cd C:\Horizon
powershell -ExecutionPolicy Bypass -File .\restore-appdata.ps1 -Zip .\Horizon-appdata.zip
```

Use `powershell` (Windows PowerShell 5.1, always present) — not `pwsh`, which is
PowerShell 7 and is usually not installed on a target station.

**4. Set the per-machine fields** (everything else is cloned as-is):

- `%APPDATA%\Horizon\config\app_settings.json` -> `stationId` (unique per station)
- `%APPDATA%\Horizon\config\camera-config.json` -> `SerialNumber` (this camera's serial)
- verify `motor_devices.json` / `camera-config.json` IPs match the rig

**5. Smoke test** in order (isolates driver issues from the UI):

1. `Horizon.ConfigTool\Horizon.ConfigTool.exe` - config loads with no cross-ref errors
2. `Horizon.Console\Horizon.Console.exe` - each device connects
3. `FAAM.UI\FAAM.UI.exe` - jog moves each axis, camera live, E-Stop trips the overlay

> Run only ONE hardware-owning app at a time (FAAM.UI *or* Console) - a single
> process owns the SDKs.

---

## Script options

| Script | Param | Meaning |
|---|---|---|
| publish.ps1 | `-Output <dir>` | staging folder (default `..\artifacts\deploy`) |
| publish.ps1 | `-Configuration` | build config, default `Release` |
| publish.ps1 | `-Apps <names>` | which of FAAM.UI / Horizon.Console / Horizon.ConfigTool to publish (default: all) |
| publish.ps1 | `-Zip` | zip each app as `<App>-v<Version>-win-x64.zip` (release asset) |
| publish.ps1 | `-Version` | version used in the zip name (default: `<Version>` from FAAM.UI.csproj) |
| publish.ps1 | `-NoVendorRuntime` | skip staging the native pylon runtime |
| publish.ps1 | `-Snapshot` | also bundle the %APPDATA%\Horizon seed |
| publish.ps1 | `-CleanHistory` | with -Snapshot: drop history.db + dut\ |
| stage-vendor-runtime.ps1 | `-Target <dir>` | published app folder to stage into (required) |
| stage-vendor-runtime.ps1 | `-PylonRoot <dir>` | override pylon SDK autodetection |
| stage-vendor-runtime.ps1 | `-RequirePylon` | error instead of warn when pylon is missing |
| release.ps1 | `-Version <n>` | release version without `v` (required); tag becomes `v<n>` |
| release.ps1 | `-Assets <files>` | attach an explicit set instead of auto-resolving `artifacts\deploy` |
| release.ps1 | `-NoAppData` | app zips only; skip the `Horizon-appdata.zip` seed |
| release.ps1 | `-UploadOnly` | don't tag; add/replace assets on the existing release + refresh its body |
| release.ps1 | `-DryRun` | print the plan, change nothing |
| release.ps1 | `-Draft` / `-PreRelease` | publish as draft / mark as pre-release |
| snapshot-appdata.ps1 | `-IncludeLogs` | also capture logs\ (normally skipped) |
| snapshot-appdata.ps1 | `-CleanHistory` | drop history.db + dut\ |
| restore-appdata.ps1 | `-Zip <file>` | seed zip to restore (required) |

---

## Cutting a GitHub release

```powershell
pwsh deploy/publish.ps1 -Zip -Snapshot -CleanHistory   # all 3 app zips + the appdata seed
pwsh deploy/release.ps1 -Version 0.1 -DryRun           # check the plan
pwsh deploy/release.ps1 -Version 0.1 -PreRelease       # tag, push, create the release
```

A release carries **all three apps**, not just the UI — that is what makes it
usable on a bare station: `Horizon.ConfigTool` configures the target's hardware,
`Horizon.Console` proves it, `FAAM.UI` runs the process. With no `-Assets`,
`release.ps1` resolves them from `artifacts\deploy`:

```
FAAM.UI-v<n>-win-x64.zip             required
Horizon.Console-v<n>-win-x64.zip     required
Horizon.ConfigTool-v<n>-win-x64.zip  required
Horizon-appdata.zip                  attached if snapshot-appdata.ps1 made one (-NoAppData to skip)
restore-appdata.ps1                  ditto
```

A missing app zip is an error, not a silent omission — the tag is coarse (`v0.1`)
while the zips carry the full triple (`0.1.0`), and both spellings resolve.

To add an asset to a release that already shipped without it, rebuild just that
app and top the release up in place (`--clobber`, and the body is refreshed from
the notes file):

```powershell
pwsh deploy/publish.ps1 -Apps Horizon.ConfigTool -Zip
pwsh deploy/release.ps1 -Version 0.1 -UploadOnly -DryRun
pwsh deploy/release.ps1 -Version 0.1 -UploadOnly
```

`release.ps1` refuses to run on a dirty tree, if the tag exists, or if the tag
disagrees with `<Version>` in `src\FAAM.UI\FAAM.UI.csproj`. Bump that property and
add a `docs/release-notes-v<n>.md` (the release body) for each new version.
Requires the GitHub CLI, authenticated once with `gh auth login`.
