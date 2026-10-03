# FAAM.UI v0.3

Third tagged release of **FAAM.UI**, the FAU-attach operator application on the
Horizon 3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2 `Horizon.Workflow`
→ Layer 3 `FAAM.UI`). Snapshot of `master` at the tag, published self-contained for
`win-x64`. Internal engineering pre-release for rig deployment.

Deployment procedure, bundled/required DLLs, the `%APPDATA%\Horizon` seed and the
bottom-up bring-up (ConfigTool → Console → FAAM.UI) are unchanged from
[v0.1](https://github.com/komcat/Horizon/releases/tag/v0.1) — see those notes and
`docs/deployment.md`. This page lists what changed since
[v0.2](https://github.com/komcat/Horizon/releases/tag/v0.2).

## Assets

| Asset | What it is |
|---|---|
| `FAAM.UI-v0.3.0-win-x64.zip` | The operator application. Extract and run `FAAM.UI.exe`; .NET 8 Desktop runtime bundled. |
| `Horizon.Console-v0.3.0-win-x64.zip` | Headless engine test rig over the same Layer 1/2 stack — prove the hardware before the operator app. |
| `Horizon.ConfigTool-v0.3.0-win-x64.zip` | Hardware config authoring utility for `%APPDATA%\Horizon\config\` (no hardware access). |
| `Horizon-appdata.zip` | The `%APPDATA%\Horizon` seed — `config\` + `products\`, with `history.db`, `dut\` and `logs\` stripped. |
| `restore-appdata.ps1` | Restores the seed on the target (run with Windows PowerShell 5.1: `powershell -ExecutionPolicy Bypass -File .\restore-appdata.ps1 -Zip .\Horizon-appdata.zip`). |

## What's new since v0.2

### Power supply
- **UNI-T UDP3305S-E over LAN** (raw SCPI socket, or VISA USBTMC / VXI-11) alongside the
  Keysight adapter; setpoints beyond the channel limits are refused before sending.
- **`psu.powerUp` — verified power-up** shared by program blocks, `.moon`
  (`machine.psu.powerUp`) and `.hrcp`: link check → current limit → voltage → ON →
  read-back, switched back **OFF** if the read-back is wrong (also on cancel).
  `psu.disconnect` forces outputs off first.
- **Run-page PSU panel** (replaces the hand-operated Dispense panel): green-on-black V/A,
  per-channel ON/OFF, CH2/CH3 add/remove, locked during a run. ON applies the channel's
  setpoints from Machine › Settings — default **CV 3.3 V / 1.0 A**, with *Reset to defaults*.
- `docs/power-supply.md`.

### Lighting (CCS PD3)
- CCS PD3-10024-8-EI adapter rewritten against the manual; per-camera light groups with
  red / green / blue / white / collimate call-outs and percent control.
- Program blocks `lighting.setLightPercent` / `setLightColor`, `.moon`
  `machine.lighting.*`, a Machine › Camera lighting panel, and optional light / colour / %
  on **Move to Position** (applied while the stage travels).

### SiPhOG labelled snapshots (new)
- **Save SiPhOG Snapshot** block / `.moon` `machine.siphog.save(label, windowMs, note)`:
  averages the six SiPhOG readings over a window (mean / min / max / std) and appends a
  labelled, operator-stamped set to `products\{product}\siphog_data\{serial}.json` — one
  file per DUT, sets stacked in order (schema `horizon.siphog-snapshots/1`).
- **DUT Data › SiPhOG snapshots** tab: per-serial sets × parameters grid, long-form CSV
  export, and six trend charts across every DUT (X = real timeline or DUT serial; one
  line per label).
- SiPhOG readings now **fail while the client is disconnected** instead of returning the
  last stale frame.

### Run page
- **Running state**: while a step executes the steps panel darkens, its title turns green
  "RUNNING…", and a progress bar shows % + the current block — mirrored big in the RUN
  header (`RUNNING…  ≈42% · 2:10 left`).
- **Progress %** counts top-level blocks, with parallel branches / sub-programs and a
  script's own `machine.progress(pct, msg)` filling the current block; after one complete
  run it is **time-weighted** from earlier run timings and estimates time left.
- **One program at a time**: a step click while anything is running is refused (no more
  silent cancel-and-restart); the step list and deck picker lock during a run. STOP stays live.
- Resizable camera column; MoveClick geometry logged for diagnosis.

### Engineer page
- **Programs list** left of Commands: search, click to open, hide/show per program
  (per product), *Show hidden* switch.
- **Command search** box over the palette.
- Best-effort blocks (**Save SAG to Sheet**, **Save SiPhOG Snapshot**) gain *Ask operator if
  it fails*: a failure pops Continue / Stop (on by default); off, the block is skipped with
  a warning and the run continues.

### Home
- **4 × 4 launcher** showing only the screens the signed-in level can open.
- **Version** tile: this build's version, git id and build time, plus the release history.

### Scripting (`.moon`)
- `machine.progress(pct, msg)`, `machine.siphog.save(...)`, `machine.signal.tryRead` (nil on
  failure).
- **STOP during a Run Script block** now ends the script cleanly as cancelled (it no longer
  trips the debugger's user-unhandled break).

### Fixes
- Hexapod absolute moves are sent as **one multi-axis MOV**, so an intermediate pose can no
  longer be rejected when the final pose is valid.
- Pylon grab / frame-convert failures are logged; `deploy/allow-firewall.ps1` adds inbound
  rules for GigE camera streams.
- Basler pylon SDK path resolved per machine (pylon 8 or 9+), HALCON version read fixed.
- Machine › Settings: *Start correction* vision U-correction toggle.

## Upgrading a station from v0.2

- No migrations. New per-station files appear on first use in `%APPDATA%\Horizon\config\`:
  `faam_psu_panel.json`, `faam_psu_defaults.json`, `faam_run_layout.json`,
  `faam_run_durations.json`, `faam_hidden_programs.json`; per product, `siphog_data\`
  (DUT history — not copied by product **Save As…**, like `dut\`).
- **Power supply over LAN**: configure the UDP3305S-E address in the power-supply device
  config (see `docs/power-supply.md`) and check the Run-page PSU defaults in Machine › Settings.
- **Lighting**: the light channels carry a `camera` field — review the lighting config for
  the rig's light groups.
- **Firewall**: run `deploy/allow-firewall.ps1` once (as Administrator) if GigE camera
  frames are dropped.
- **Existing Save SAG to Sheet blocks** now ask Continue / Stop on failure by default —
  untick *Ask operator if it fails* on a block to keep the old silent skip.

## Known limitations

- Run-page process steps remain **Phase 1 / hardcoded** in `Views/ProcessStepsPanel`
  when a product has no deck.
- Progress estimates need one complete earlier run of a program; a first run counts blocks.
- HALCON: the projects reference the **24.05** managed wrapper; verify vision ops on a
  target with a different HALCON major.
- The solution only builds on a box with the vendor SDKs installed.
