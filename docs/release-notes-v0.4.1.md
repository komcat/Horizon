# FAAM.UI v0.4.1

Patch release of **FAAM.UI**, the FAU-attach operator application on the Horizon
3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2 `Horizon.Workflow` → Layer 3
`FAAM.UI`). Snapshot of `master` at the tag, published self-contained for `win-x64`.
Internal engineering pre-release for rig deployment.

Deployment procedure, bundled/required DLLs, the `%APPDATA%\Horizon` seed and the
bottom-up bring-up (ConfigTool → Console → FAAM.UI) are unchanged from
[v0.1](https://github.com/komcat/Horizon/releases/tag/v0.1) — see those notes and
`docs/deployment.md`. This page lists what changed since
[v0.4](https://github.com/komcat/Horizon/releases/tag/v0.4).

## Assets

| Asset | What it is |
|---|---|
| `FAAM.UI-v0.4.1-win-x64.zip` | The operator application. Extract and run `FAAM.UI.exe`; .NET 8 Desktop runtime bundled. |
| `Horizon.Console-v0.4.1-win-x64.zip` | Headless engine test rig over the same Layer 1/2 stack — prove the hardware before the operator app. |
| `Horizon.ConfigTool-v0.4.1-win-x64.zip` | Hardware config authoring utility for `%APPDATA%\Horizon\config\` (no hardware access). |
| `Horizon-appdata.zip` | The `%APPDATA%\Horizon` seed — `config\` + `products\`, with `history.db`, `dut\`, `siphog_data\` and `logs\` stripped. |
| `restore-appdata.ps1` | Restores the seed on the target (run with Windows PowerShell 5.1: `powershell -ExecutionPolicy Bypass -File .\restore-appdata.ps1 -Zip .\Horizon-appdata.zip`). |

## What's new since v0.4

### Jog rail — Camera / Lighting tabs
- The rail's camera box is now a **Camera** tab (exposure, auto-exposure, presets —
  unchanged) beside a new **Lighting** tab: Connect / Disconnect / All off, and per light a
  0–100 % slider, typed %, Off / 100 %, and the colour call-outs the light has channels for
  (Red / Green / Blue / White, Collimate, White + Collimate). The lights shown follow the
  camera picked on the Camera tab; opening the tab re-reads the controller's levels.

### Machine › Motion — position map + grid
- **Position grid**: the positions list is a grid (Map tick, Name, one column per installed
  axis). Move / Update / Edit… act on the selected row (double-click = Edit); Update shows
  stored vs current before re-teaching. Move / Update need the device connected and no run.
- **Position map** beside the grid (draggable splitter): circle nodes per position (Map tick,
  or drag a name onto the canvas), Link mode joins two nodes, right-click / Delete removes. A
  node lights green while the device stands on it; a selected node offers Move / Update, with
  an arrow from the current node to the target (dashed preview, solid while moving). Layout
  persists per product + device in `faam_position_map.json`.
- The per-axis panel is transposed: one column per axis with smaller − / + jog buttons.

### Machine page
- The section list's selected item is a solid highlight with bold, larger (16 pt) text — it
  was near-invisible before.

### Camera overlay / DUT Data
- **Tool overlay show/hide** (Machine › Settings › Camera, default **hidden**): hides the
  gripper / gripped-FAU crosshair, outline and label on the Run-page feeds and camera pop-out.
  The Gripped FAU, Part Alignment and Registration pages always show it.
- DUT Data: **Open folder** beside Export CSV on both tabs opens the product's `dut\` or
  `siphog_data\` folder.

### Deployment
- `deploy\build-deploy.bat` — double-click wrapper around
  `publish.ps1 -Snapshot -CleanHistory` that stages the three apps plus the seed into
  `C:\Horizon-deploy` (or a folder passed as the first argument).

## Upgrading a station from v0.4

- No migrations. New per-station file `faam_position_map.json` is created on first use of
  the position map.
- The tool overlay now defaults to **hidden** — tick Machine › Settings › Camera › "Show the gripper / gripped-FAU
  overlay…" to get it back on the Run page.

## Known limitations

Unchanged from [v0.4](https://github.com/komcat/Horizon/releases/tag/v0.4).
