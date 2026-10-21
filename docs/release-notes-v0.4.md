# FAAM.UI v0.4

Fourth tagged release of **FAAM.UI**, the FAU-attach operator application on the
Horizon 3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2 `Horizon.Workflow`
→ Layer 3 `FAAM.UI`). Snapshot of `master` at the tag, published self-contained for
`win-x64`. Internal engineering pre-release for rig deployment.

Deployment procedure, bundled/required DLLs, the `%APPDATA%\Horizon` seed and the
bottom-up bring-up (ConfigTool → Console → FAAM.UI) are unchanged from
[v0.1](https://github.com/komcat/Horizon/releases/tag/v0.1) — see those notes and
`docs/deployment.md`. This page lists what changed since
[v0.3](https://github.com/komcat/Horizon/releases/tag/v0.3).

## Assets

| Asset | What it is |
|---|---|
| `FAAM.UI-v0.4.0-win-x64.zip` | The operator application. Extract and run `FAAM.UI.exe`; .NET 8 Desktop runtime bundled. |
| `Horizon.Console-v0.4.0-win-x64.zip` | Headless engine test rig over the same Layer 1/2 stack — prove the hardware before the operator app. |
| `Horizon.ConfigTool-v0.4.0-win-x64.zip` | Hardware config authoring utility for `%APPDATA%\Horizon\config\` (no hardware access). |
| `Horizon-appdata.zip` | The `%APPDATA%\Horizon` seed — `config\` + `products\`, with `history.db`, `dut\`, `siphog_data\` and `logs\` stripped. |
| `restore-appdata.ps1` | Restores the seed on the target (run with Windows PowerShell 5.1: `powershell -ExecutionPolicy Bypass -File .\restore-appdata.ps1 -Zip .\Horizon-appdata.zip`). |

## What's new since v0.3

### Machine › IO Watch (new section)
- Read-only view of the engine's IO watchers: io-watch / safety-watch status, every
  `io_watch.json` binding with live **input / output / indicator** state dots and its last
  fire time, the safety-watched inputs, and an event log of binding fires and safety
  trips / clears.
- **Switch LED indicators**: a binding may name an `"indicator"` output (e.g.
  `IOSwitch.switch_led_2`) that the IO watch keeps equal to the bound output's real state
  every poll — whatever drove it (the switch, a program block, the Machine page). The seed's
  bindings for switches 2–7 and 10 light their own `switch_led_N`. Editable in
  `Horizon.ConfigTool` (IO watch › Indicator, `Device.Pin` or `-`); shown in
  `Horizon.Console`.

### New program blocks
- **`motion.slowApproach`** — teach-or-approach a final position. First run: manual jog
  popup, position saved under the block's name on Continue. Later runs: offers an automatic
  approach at 0.2 mm/s, one axis at a time (Z, Y, X) under a large STOP window, restoring the
  velocity afterwards (also on STOP / fault), then the jog popup for fine adjust. A refused,
  failed or stopped approach falls back to the manual jog.
- **`io.alertBuzzer`** — an always-on-top yellow alert (e.g. "UV complete") with a pulsed
  buzzer output until the operator presses Acknowledge; STOP also silences it. A buzzer IO
  fault is logged and does not fail the run.
- **`dut.promptText`** (*DUT Text Entry*) — the operator types free text that is filed in
  the open DUT record under `key` (optionally also a context variable). Cancel fails the run
  unless `required` is off.

### Run page
- The **active DUT serial** is shown large on the live camera view (top-centre,
  click-through) and above the process-steps title while a serial is set.
- The **capture prompt** also appears, compact, on the Zoom2× inset. A cancelled / aborted
  step no longer latches the "reload the part and run step (1)" gate.
- **Jog popup** remembers the picked step size (`faam_jog_pad.json`); step chips have
  higher contrast.

### Engineer
- Position drop-downs re-read the device's saved positions each time they open, so a position
  taught elsewhere (Machine page, a `slowApproach` run) appears without reopening the editor.

### Machine › Motion — gantry-main (ACS)
- **Axis enable**: each axis row shows an Enabled / Disabled indicator (polled from the
  controller) with an **Enable / Disable** button, plus **Enable all / Disable all**.
  Disabling asks for confirmation — an unbraked axis may drift or drop.
- **Buffer 1**: running indicator, **Run buffer 1** sends the terminal line
  `START 1, STARTUP`, and a red **Stop buffer 1** (always available while connected) sends
  `STOP 1` and then stops all gantry motion — a safety stop for the program and any move it
  had already commanded. A rejected command (`?` reply) is reported with the controller's text.
- **Move Z → 25 mm** one-click absolute move.

### Machine › Motion — hex-right (PI C-887)
- **Reference** button: references all six axes (FRF) after a confirm, with a Referenced /
  Not referenced / Referencing… indicator. STOP aborts it; 3-minute timeout.
- **Absolute move per axis**: a target box + **Move** on every X/Y/Z/U/V/W row.

### Hexapod pivot point (new)
- Machine › Settings › **Hexapod pivot point**: R / S / T per hexapod, default
  **R = −14.52, S = 0, T = 111**, saved to `faam_hexapod_pivot.json`. **Read** shows what the
  controller holds; **Apply** writes the typed values now; *Reset to default*.
- The C-887 forgets its pivot at power-down (0, 0, 0). FAAM now reads it **at startup** (after
  connect) and **after a Reference**, and writes the configured pivot back whenever the
  controller's differs. Every check / restore is logged.

### Find First Light
- Stage 1 now sweeps **Y ±50 µm at 10 µm pitch after each −X line step**, and stops and parks
  as soon as any reading exceeds an absolute SAG level (*ExitAboveV*, default 0.1 V), then
  hands off to the hill climb. Stage 2 never sweeps Y. Settings on Engineer › Find First Light.

### SiPhOG
- **Snapshots grouped into runs**: each saved set carries a run id — a run begins when the
  serial is entered and ends at `dut.close`, Run-page Pass / Fail, serial clear or product
  switch — so a re-run serial never mixes attempts. DUT Data › SiPhOG snapshots gains a
  **Run** picker and refreshes live when a snapshot is saved.
- `.moon` **`machine.siphog.setCurrent(mA)`** — sets the SLED drive current (`SET_CURRENT` on
  the SiPhOG server); fails the script if the server refuses it.

### Scripting (`.moon`)
- **Long scripts no longer stop part-way**: auto-yielded coroutines are now resumed, so a
  script past ~20k instructions (e.g. UV_SAA4) runs to the end instead of being reported as
  "Script completed" early.

### Operator prompts
- Solid blue, larger operator prompt and serial-entry windows.

## Upgrading a station from v0.3

- No migrations. `io_watch.json` bindings without an `"indicator"` behave as before.
- New per-station files: `faam_jog_pad.json` (on the first jog step pick) and, on first Save
  in Machine › Settings, `%APPDATA%\Horizon\config\faam_hexapod_pivot.json`. Without the
  pivot file the default pivot
  (−14.52 / 0 / 111) is used — **check it matches the rig's tooling before first use**, since
  FAAM writes it to the hexapod at startup.
- The controller may refuse a pivot change while U / V / W are not zero; the startup log then
  shows PI's error. A **Reference** (which ends at U/V/W = 0) re-applies it.
- Gantry **Run buffer 1** expects a `STARTUP` label in ACS buffer 1.

## Known limitations

- Run-page process steps remain **Phase 1 / hardcoded** in `Views/ProcessStepsPanel`
  when a product has no deck.
- The gantry buffer number / label and the Z → 25 mm target are build constants
  (`MotionSectionViewModel`), not settings.
- HALCON: the projects reference the **24.05** managed wrapper; verify vision ops on a
  target with a different HALCON major.
- The solution only builds on a box with the vendor SDKs installed.
