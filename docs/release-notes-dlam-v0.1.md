# DLAM.UI dlam-v0.1

First release of **DLAM.UI**, the double-lens active-alignment operator application (the
FireDog machine) on the Horizon 3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2
`Horizon.Workflow` → Layer 3 `DLAM.UI`). Snapshot of `master` at the tag, published
self-contained for `win-x64`. Internal engineering pre-release for rig bring-up, run side by
side with FireDog, never at the same time.

DLAM.UI shares its shell with FAAM.UI (the `Horizon.Station.UI` library: startup, login, Home,
Machine, Engineer, Repository, OEE, Version / update). It releases separately: tags `dlam-v*`
and its own update channel (`dlam`), so a FAAM station is never offered a DLAM build and the
reverse.

## Assets

| Asset | What it is |
|---|---|
| `DLAM.UI-dlam-Setup.exe` | Installs DLAM.UI so it can update itself (to `%LOCALAPPDATA%\DLAM.UI`). Use this. |
| `DLAM.UI-dlam-Portable.zip` | The same self-updating install, unzipped into a folder of your choice instead. |
| `DLAM.UI-0.1.0-dlam-full.nupkg`, `releases.dlam.json`, … | The update packages installed stations download — not for manual use. |
| `DLAM.UI-v0.1.0-win-x64.zip` | The operator application as a plain folder (no self-update). Extract and run `DLAM.UI.exe`; .NET 8 Desktop runtime bundled. |
| `Horizon.Console-v0.1.0-win-x64.zip` | Headless engine test rig over the same Layer 1/2 stack, built from the same commit — prove the hardware before the operator app. |
| `Horizon.ConfigTool-v0.1.0-win-x64.zip` | Hardware config authoring utility for `%APPDATA%\Horizon\config\`, including **Import from FireDog** (`--import-firedog`), which writes the device config from FireDog's `hardware.json`. |

No `%APPDATA%\Horizon` seed ships with this release: the config comes from FireDog through the
ConfigTool import, and the product data from `tools/dlam/firedog_port.py` (in the repo).

## What's in it

### Operator app
- **Run page:** deck steps (STOP, Auto-chain), camera column (MoveClick jog, Zoom 2×, snapshot
  box), log, and the **alignment band**: live left / right lens poses and Keithley ch1 / ch2
  readings, with buttons for Left / Right lens XCAN, Climb and the two-lens XCAN. Band presets
  are on Machine › Settings › Run page alignment band.
- **DUT Data page:** serial → runs → recorded values, CSV export.
- FireDog's mascot in the header follows the station state (awake / working / sleeping / down).

### FireDog ports (Layer 2 program blocks)
- `scan.xcan`, `scan.xcanTwoLens` — FireDog's XCAN coordinate descent (adaptive ladder,
  stop-and-stay exit; two-lens = left coarse → left coarse → left fine → right coarse).
- `scan.climb` — FireDog's climb raster.
- `motion.liftOff`, `motion.movePath` (routes along the position map), `io.waitInput`,
  `flow.waitUntil`.
- Every scan parks on its best point on STOP or error.

### Ported FireDog recipes
"PROD Tall AUTO V3": Initial Machine, Probe SLED and PIC, PP Left - Array, PP Right - Array and
UV advance1, as Horizon programs plus the deck, positions, route maps and the `CaptureView` /
`UV_Advanced` scripts. Two new opt-in block options came with them:
- **Power-up › Accept current-limit (CC) mode as ON** — FireDog powers the laser in CC mode; a
  load held at its current limit now counts as a successful power-up instead of a fault.
- **Ask serial › New unit** — always asks for a fresh serial instead of reusing the previous
  unit's.

## Installing on the DLAM rig

- **HALCON 25.11** runtime must be installed (same version as the wrapper).
- Close FireDog. Only one of FireDog / DLAM.UI / FAAM.UI / Horizon.Console may run at a time —
  one process owns the hardware SDKs.
- Run `DLAM.UI-dlam-Setup.exe`, then `allow-firewall.ps1` from the install's `current\` folder so
  the camera stream gets through.
- If `%APPDATA%\Horizon\config\` is not set up yet: `Horizon.ConfigTool --import-firedog`.
- First run: sign in, check the product is `DLAM`, step through the ported programs at low jog
  velocity before running a unit.

## Known limitations

- First cut, not yet proven on the rig: the band scans and the ported programs need a supervised
  first run.
- Of "PROD Tall AUTO V3", the scan, SMU / TEC init and dispense steps are not ported yet, so the
  deck's Auto-chain is off.
- The update packages are not code-signed: Windows SmartScreen may warn the first time
  `DLAM.UI-dlam-Setup.exe` runs ("More info" › "Run anyway").
