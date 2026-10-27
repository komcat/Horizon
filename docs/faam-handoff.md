# FAAM.UI — handoff (resume tomorrow)

**Last commit:** `5ae4409` "Add FAAM.UI operator app; SystemColors theme for
Horizon.UI" — pushed to `origin/master`. Working tree clean.

Reference: `docs/faam-ui.md` (overview), and the project memory notes.

---

## ✅ Working on hardware (verified today)

- Engine brings up clean: 2 IO, 2 motion (`gantry-main`, `hex-right`), 2 pneumatic
  slides, 1 camera, vision (HALCON 25.05), safety. Clean shutdown (after the poll-
  timer fix).
- **Jog panel** (shell left rail): device select, velocity presets (mm/s, family-
  based), step-size presets (0.1 µm … 5 mm), live X/Y/Z/U/V/W readout, STOP. Gantry
  and hex both jog; global→local transform applied.
- **MoveClick** (Run page): arm → click feed/Zoom2x → measures mm from crosshair and
  jogs `gantry-main` (coordinated X+Y, simultaneous). Cyan crosshair + cursor when
  armed.
- **Camera feed**: aspect-fit (no black bars), crosshair + ROI, Zoom2x.
- **Process panel**: timer, STOP (halts all motion), Teach/Run toggle.

## ⚠️ Needs bench calibration (do first, quick)

1. **Jog transform signs** — `%APPDATA%\Horizon\config\jog_transforms.json` (seeded
   with the provided matrices). For each device, jog each global axis slowly and
   confirm direction; flip matrix signs if inverted.
2. **MoveClick signs** — Machine → Settings → `X sign` / `Y sign` (image Y usually
   needs `-1`). Then tune **`mm/pixel`** against a known on-screen distance.
3. **Jog feel** — velocity + step presets are editable (Settings for velocity;
   steps are fixed in code `StepSizes`). Adjust if too coarse/fine.
4. **Verify seeded teach points** — `faam_teach_points.json` was seeded from
   `acs_solution_saa`'s `PositionsGantry.json` (`Home`, `FAU`, `PIC`, `UV`). These
   are absolute ACS machine coordinates and only replay correctly if `gantry-main`
   shares the same ACS zero/home as the old app. With Teach OFF, click step (1) and
   confirm the gantry lands at Home (≈ 37, 45, 15); re-teach if off. `Dispense`
   (4.0) and `Unload` (5) have no ACS equivalent — teach them on the bench.

---

## 🔧 Next tasks (pick up here)

### 1. Finish the FAU-attach process (Phase 1 hardcoded)
`src/FAAM.UI/Views/ProcessStepsPanel.xaml.cs` — `RunStepAsync` + the constants block.
Mappings already wired: `gantry-main` moves to taught points; gripper `R_Gripper`
on `IOBottom` (on=close); `UV_Head` / `Dispenser_Head` pneumatic slides.
- **DONE: dispense valve pin** — `DispensePin = "Dispenser_Shot"` (from
  `acs_solution_saa` `OutputPinMapping.json`, pin 15). Step 4.1 now pulses the real
  output (on → `DispenseMs` → off). Verify wiring matches on the bench.
- **DONE: reusable position names** — teach points are now keyed by a per-step
  `Position` name (`Home`/`FAU`/`PIC`/`Dispense`/`UV`/`Unload`), modeled on the ACS
  names, instead of the verbose step label. Names can be shared across steps to
  reuse a pose (e.g. point step 5's `Unload` at `FAU`/`Home`). 4.2/4.3 have no
  `Position` (dwell-only, no move).
- **OPEN: sensor waits** — currently `ExtendAsync`/`RetractAsync` (fire, no wait). If
  the slides have extended/retracted sensors, switch to `ExtendAndWaitAsync` /
  `RetractAndWaitAsync` so steps block until confirmed.
- **OPEN: UV/dispense timings + exact sequence** — `UvDwellMs`, `DispenseMs`, and
  whether step 4.1/4.2/4.3 do what's intended.
- **Teach the positions**: `Home`/`FAU`/`PIC`/`UV` seeded from ACS (verify on
  bench, see calibration #4). Still to teach: `Dispense` (4.0) and `Unload` (5) —
  arm Teach, jog there, click the step (saved to `faam_teach_points.json`).

### 2. Properties editor (button exists, placeholder)
`ProcessStepsPanel.OnProperties`. Per-step NON-motion settings: IO pin names,
inbound/outbound (debounce) delays, wait/dwell times, camera exposure. Decide the
data model + a small editor dialog.

### 3. Guided teach (button works; wizard TODO)
`ProcessStepsPanel.MoveToTaughtAsync` + `IsSuspectZero` (0,0,0 guard in place).
**Open question for the user:** wizard from one button (steps through each untaught
position in order) vs. per-step (operator clicks each in Teach mode)?

### 4. Phase 2 (later) — promote process to Layer 2
Define the flow as a `WorkflowDeck` + programs (`IProcessService.RunProgramAsync`),
move teach points to `PositionStore`, and port Horizon.UI's Program Editor + Deck
pages to FAAM. Teach data is already file-persisted, so the bridge is cheap.

---

## ❓ Questions for the user

- ~~Dispense valve IO output pin name?~~ — answered: `Dispenser_Shot` (set).
- Do the pneumatic slides have sensors (use `…AndWaitAsync`)?
- Guided-teach UX: wizard vs. per-step?
- UV cure sequence/timing for steps 4.1–4.3?

---

## Gotchas / reminders

- **One process owns the SDKs** — don't run FAAM.UI and Horizon.UI together.
- **NuGet restore** after any package change (e.g. SkiaSharp) — a plain build can use
  a stale `obj`; restart VS or restore the solution.
- **HALCON** — app runs without it (graceful). The wrapper is 25.11 and the native
  `halcon.dll` comes from the installed HALCON on `PATH`; the two must be the same version
  or operators misroute (HALCON #1202 naming an operator the code never calls).
- **Shutdown rule** — any UI poller/timer touching an engine facet MUST be stopped in
  `ShellWindow.OnClosing` before teardown (facet accessors throw post-shutdown). Now
  stops: `_run.Deactivate()`, `_machineVm.Dispose()`, `JogPanelControl.Stop()`.
- **Config dir** (`%APPDATA%\Horizon\config\`): `app_settings.json`,
  `jog_transforms.json`, `faam_teach_points.json` (+ shared engine device configs).

## Build / run
VS, x64, restore NuGet, set **FAAM.UI** as startup project. Packages:
`MVTec.HalconDotNet-Windows`, `SkiaSharp.Views.WPF`.
