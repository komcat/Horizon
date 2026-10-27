# FAAM.UI v0.6

Feature release of **FAAM.UI**, the FAU-attach operator application on the Horizon
3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2 `Horizon.Workflow` → Layer 3
`FAAM.UI`). Snapshot of `master` at the tag, published self-contained for `win-x64`.
Internal engineering pre-release for rig deployment.

Deployment procedure, bundled/required DLLs and the bottom-up bring-up
(ConfigTool → Console → FAAM.UI) are unchanged from
[v0.1](https://github.com/komcat/Horizon/releases/tag/v0.1) — see those notes and
`docs/deployment.md`. This page lists what changed since
[v0.5](https://github.com/komcat/Horizon/releases/tag/v0.5).

## Assets

| Asset | What it is |
|---|---|
| `FAAM.UI-win-Setup.exe` | **New:** installs FAAM.UI so it can update itself (to `%LOCALAPPDATA%\FAAM.UI`). Use this for new and existing stations. |
| `FAAM.UI-win-Portable.zip` | **New:** the same self-updating install, unzipped into a folder of your choice instead. |
| `FAAM.UI-0.6.0-full.nupkg`, `releases.win.json`, … | **New:** the update packages installed stations download — not for manual use. |
| `FAAM.UI-v0.6.0-win-x64.zip` | The operator application as a plain folder (no self-update). Extract and run `FAAM.UI.exe`; .NET 8 Desktop runtime bundled. |
| `Horizon.Console-v0.6.0-win-x64.zip` | Headless engine test rig over the same Layer 1/2 stack — prove the hardware before the operator app. |
| `Horizon.ConfigTool-v0.6.0-win-x64.zip` | Hardware config authoring utility for `%APPDATA%\Horizon\config\` (no hardware access). |
| `Horizon-scripts-v0.6.0.zip` | Just the `.moon` scripts, laid out as `products\<name>\scripts\`. |

## What's new since v0.5

### Software update over the internet
- **Home › Version › Update…** installs a newer FAAM.UI release from GitHub. The station
  checks quietly after start-up; the Version tile shows "v… available" when there is one.
- Engineer level, refused while a program / deck, Run-page step or alignment runs, one
  confirmation. FAAM downloads the update (only the changed files when possible),
  disconnects all hardware, exits, installs and restarts by itself.
- `%APPDATA%\Horizon` (config, products, users, history, OEE, logs) is never touched.
  Update only — no rollback from the UI; back up `%APPDATA%\Horizon` by hand before a
  release that says it changes data formats.
- Works only for a copy installed from `FAAM.UI-win-Setup.exe` / `-Portable.zip` — see
  *Upgrading a station* below. Design: `docs/software-update-design.md`.

### Alignment blocks
- **U Correction** (`align.uCorrection`) and **Accuracy Place FAU** (`align.placeFau`)
  program blocks; **U → FAU Correction** (`align.correction`) runs the Run-page chain from a
  program / deck. Each carries its 4 points, PIC-FAU gap, target angle, waveguide angle (was
  a hardcoded 7°), Max U and Max FAU move — blank = the Run-page setting.
- Accuracy place FAU refuses a total hexapod move over **0.5 mm** (`MaxCorrectionMm`) before
  anything moves; repeated points are refused. "FAU pre-align" is renamed "Accuracy place FAU".
- **Go / Teach here** buttons beside the align blocks' gantry point pickers (Engineer page;
  refused while anything runs).
- **FAU script blocks** `fau.gridScan` / `fau.powerClimb` / `fau.wOptimize` — typed front-ends
  for the product's `FAU_GridScan` / `FAU_PowerClimb` / `FAU_WOptimize` scripts.
- **Find First Light** detects signal relative to the unit's own noise floor (running median +
  max(K × σ, min Δ), re-measured) instead of a fixed voltage; stops at the first confirmed point.
- New default script preset **FAU W optimize**.

### Run page
- Right-click a deck step › **Edit** opens its program in the Engineer timeline editor.

### Hardware
- **Siglent SPD1305X** power supply adapter (USBTMC or LAN), config type `siglent_spd1305x`.
- **HALCON 25.11**: the managed wrapper now matches the 25.11 runtime. A mismatched wrapper
  broke every vision detection with a search ROI (HALCON error #1202).

## Upgrading a station from v0.5

- **HALCON 25.11** runtime must be installed (same version as the wrapper).
- **Switch to the self-updating install once:** run `FAAM.UI-win-Setup.exe` (or unzip
  `FAAM.UI-win-Portable.zip` into a fixed folder), then run `allow-firewall.ps1` from the new
  install's `current\` folder so the camera stream gets through. `%APPDATA%\Horizon` carries
  over unchanged. From then on every release arrives through Home › Version › Update.
- Copy in the scripts bundle so the FAU script blocks find `FAU_GridScan` / `FAU_WOptimize`.

## Known limitations

- The update packages are not code-signed: Windows SmartScreen may warn the first time
  `FAAM.UI-win-Setup.exe` runs ("More info" › "Run anyway").
- Otherwise unchanged from [v0.4](https://github.com/komcat/Horizon/releases/tag/v0.4).
