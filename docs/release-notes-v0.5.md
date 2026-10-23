# FAAM.UI v0.5

Feature release of **FAAM.UI**, the FAU-attach operator application on the Horizon
3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2 `Horizon.Workflow` → Layer 3
`FAAM.UI`). Snapshot of `master` at the tag, published self-contained for `win-x64`.
Internal engineering pre-release for rig deployment.

Deployment procedure, bundled/required DLLs, the `%APPDATA%\Horizon` seed and the
bottom-up bring-up (ConfigTool → Console → FAAM.UI) are unchanged from
[v0.1](https://github.com/komcat/Horizon/releases/tag/v0.1) — see those notes and
`docs/deployment.md`. This page lists what changed since
[v0.4.1](https://github.com/komcat/Horizon/releases/tag/v0.4.1).

## Assets

| Asset | What it is |
|---|---|
| `FAAM.UI-v0.5.0-win-x64.zip` | The operator application. Extract and run `FAAM.UI.exe`; .NET 8 Desktop runtime bundled. |
| `Horizon.Console-v0.5.0-win-x64.zip` | Headless engine test rig over the same Layer 1/2 stack — prove the hardware before the operator app. |
| `Horizon.ConfigTool-v0.5.0-win-x64.zip` | Hardware config authoring utility for `%APPDATA%\Horizon\config\` (no hardware access). |
| `Horizon-appdata.zip` | The `%APPDATA%\Horizon` seed — `config\` + `products\`, with `history.db`, `dut\`, `siphog_data\` and `logs\` stripped. |
| `restore-appdata.ps1` | Restores the seed on the target (run with Windows PowerShell 5.1: `powershell -ExecutionPolicy Bypass -File .\restore-appdata.ps1 -Zip .\Horizon-appdata.zip`). |
| `Horizon-scripts-v0.5.0.zip` | **New:** just the `.moon` scripts, laid out as `products\<name>\scripts\` — for updating a station's scripts without restoring the whole seed. See *Script bundle* below. |

## What's new since v0.4.1

### Run page — Scanning panel + FAU power climb
- **Scanning panel** (under the alignment + SiPhOG panels): small **Save1 / Save2** buttons
  store the current hex-right pose, **Move1 / Move2** below move back to it; four (2×2)
  script buttons run an assigned **script preset** or bare `.moon` script. Captions / poses /
  scripts are edited by right-click or on Machine › Settings (`faam_scan_panel.json`).
- **`FAU_PowerClimb.moon`** — coarse-to-fine SiPhOG SAG power climb for FAU→PIC: Z+ climb
  with Y re-centring, then Y± and X± correction per step set, Gaussian sub-step peak fit,
  noise-aware gain threshold, every point recorded and a final return to the global peak.
  Z travel is metered against the start (default 40 µm, cap 45 µm), XY ±30 µm; a move past
  a limit is refused, never clamped. Exits early on the photo-current-tiered SAG target
  (> 800 µA → 2 V, 500–800 → 1.5 V, 300–500 → 1.1 V).
- **STOP returns the FAU to its peak** — new `machine.onAbort(fn)`: on STOP or a script error
  the engine runs the handler once with a fresh 60 s token.
- **Raster Climb and Auto W Maximizer are removed and deprecated** (Run page and Engineer ›
  Testing Ground). After FAU pre-align the alignment chain stops at the XY grid search's
  found signal — run a Scanning preset to climb. `app.*` steps naming `hillclimb` / `autow`
  now fail with a deprecation message.

### Script presets + arguments
- **Script presets** (Machine › Settings, `faam_script_presets.json`): name + script +
  argument text — one script, several configurations. Seeded with *FAU climb - coarse*,
  *FAU climb - fine* and *FAU XY only*.
- **No exit threshold** per preset (adds `exit = false`): its Scanning button turns orange
  (default blue).
- **Script arguments**: a Lua table literal handed to the script as the global `args` —
  from a preset, the Run Script block's new *Arguments* field, or the Script editor's Args box.
- New script calls: `machine.jog` (non-modal jog popup), `motion.movePose`,
  `motion.teachPoint` / `savePosition`, and `moveTo` camera / exposure / velocity overrides.
- Script editor: view-only code folding (blocks, long comments, `--#group` regions).

### Station OEE + tower light
- **OEE page** (Home › OEE): live state, 24 h timeline, uptime / availability / utilization /
  quality / OEE, downtime log, 7-day table (`%APPDATA%\Horizon\oee.db`).
- **DOWN / UP toggle** (Engineer + OEE pages) records a maintenance-log entry — name +
  category + reason on DOWN, name + root cause + action taken on UP; the Maintenance history
  window searches, summarises (MTTR, by category) and exports CSV.
- **Tower light** (`faam_tower_light.json`) follows the state: green running, green blink
  standby, yellow idle, yellow blink waiting on the operator, red down, red blink + beeps on a
  safety stop. **Declaring DOWN sounds the buzzer for 3 s** (`downBuzzerSeconds`), then stops.
- Shell header: state-coloured info bar (UP / IDLE / DOWN), working folder + AppData button,
  clock and the pika mascot.

### Motion
- **Move to Position approach**: optional approach distance / velocity — fast to a point
  short of the target, confirm it stopped, finish slow, restore the velocity (also on STOP).
  `motion.slowApproach` is **deprecated**; existing programs still run.
- Jog rail **Move map** tab (view-only position map); **Follow path** on both maps moves leg
  by leg along the links, confirming arrival after each leg.
- Lighting panels list every light on the controller (the selected camera's first).

### Deployment
- `allow-firewall.ps1` ships inside the FAAM.UI and Horizon.Console folders; fixed its empty
  `-Path` under Windows PowerShell 5.1.
- New `deploy/bundle-scripts.ps1` and `publish.ps1 -Scripts` build the scripts bundle;
  `release.ps1` attaches it.

## Script bundle

`Horizon-scripts-v0.5.0.zip` mirrors `%APPDATA%\Horizon`. With FAAM.UI closed, on the target:

```powershell
Expand-Archive .\Horizon-scripts-v0.5.0.zip -DestinationPath "$env:APPDATA\Horizon" -Force
```

Same-named scripts are replaced; scripts only the station has are left alone. Script presets
are station config and are **not** in this zip — while `faam_script_presets.json` is missing, FAAM
offers the three default FAU climb presets.

## Upgrading a station from v0.4.1

- `oee.db` downtime gains category / root cause / action taken columns — migrated in place.
- New per-station files are created on first use: `faam_scan_panel.json`,
  `faam_script_presets.json`, `faam_tower_light.json` (check the IO device + pin names match
  the rig's tower light).
- Programs with `app.start` / `app.stop` on `hillclimb` or `autow` must be changed to a
  Scanning preset / Run Script block.
- Copy in the scripts bundle so `FAU_PowerClimb.moon` is present for the Scanning presets.

## Known limitations

Unchanged from [v0.4](https://github.com/komcat/Horizon/releases/tag/v0.4).
