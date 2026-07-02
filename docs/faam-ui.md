# FAAM.UI — operator application

`FAAM.UI` is a second **Layer 3** operator app (WPF, `net8.0-windows`), running in
parallel to `Horizon.UI` on the *same* Layer 1/2 engine stack. It is a separate
"head" purpose-built for the FAU-attach process, not a fork of Horizon.UI.

> One process owns the hardware SDKs — **do not run FAAM.UI and Horizon.UI at the
> same time** against the same rig. They also share `%APPDATA%\Horizon\config\`.

## Architecture

References the layer interfaces only — `Horizon.Engine` (Layer 1) +
`Horizon.Workflow` (Layer 2) + `Horizon.Common` — exactly like Horizon.UI. Never
the hardware adapters or the legacy `Core`/`Process` stack.

- `EngineHost` — composition root: owns the single `HorizonEngine`, `ProcessService`,
  `LogHub`, `TelemetryStore`, and `AppSettingsStore`.
- `ShellWindow` — fixed chrome (Home button, **global jog rail**, page header) around
  a swappable page host: **Home**, **Machine**, **Run**.
- Theming: Windows **SystemColors** palette (`App.xaml` brushes `Bg/Panel/Text/
  Subtle/Edge/Accent` + fixed status/video colours) — see `faam-ui` memory / the
  same scheme is mirrored in Horizon.UI.

## Pages

- **Home** — launcher tiles (RUN / Machine).
- **Machine** — the Horizon.UI Machine page ported wholesale (section rail:
  Status, Motion, IO, Pneumatic, Laser, TEC, Power supply, Measurement, SMU,
  Camera, **Settings**). Engine status + Reconnect live in the shell top bar.
- **Run** — the production page:
  - **Camera feed** — aspect-fit (≤ 1280×1024, no letterbox), crosshair + centre
    ROI, **Zoom2×** inset (centre 200×200 px), a security-camera thumbnail wall.
  - **MoveClick** — arm the toggle, click the feed/zoom to measure mm from the
    crosshair centre (`mm/pixel`) and jog `gantry-main` (coordinated X+Y).
  - **Process steps panel** (left) — FAU-attach steps, process timer, STOP,
    **Teach/Run** toggle, Properties button.
  - **Log** — engine + process/jog log lines.

## Global jog panel (shell rail)

Lists connected motion devices; X/Y/Z + U/V/W jog pad; **velocity presets** (mm/s,
family-based) and **step-size presets** (0.1 µm … 5 mm). Each jog click moves the
selected step at the selected velocity. Jog buttons are in the **global** frame and
converted to device-local via per-device transforms. Live X/Y/Z/U/V/W readout and a
STOP that halts all motion.

## Operator activity → DUT log

Manual jogs and the external "Start correction" chain (U-correction → FAU pre-align
→ grid search → hill climb → Auto W) used to leave **no trace** in a unit's DUT
record, so cycle-time / operator-effort analysis had nothing to work from. A thin
publish/subscribe seam mirrors those actions into the open DUT run **without
hardwiring** DUT-logging into any routine.

```
Publishers (Layer 3)          OperatorActivityHub        Subscribers
────────────────────          (EngineHost.Activities)    ───────────
JogPanel.JogOnceAsync   ──┐        Publish() /            ┌─▶ LogHub (log panel + Serilog file)
AlignmentPanel sections ──┼──▶  OperatorActivity ─event──┤
HillClimb / AutoW panels ─┘     (Category, action, Data)  └─▶ DutActivityMirror ─▶ IProcessService.RecordDutValue
```

- **`OperatorActivityHub`** (`EngineHost.Activities`) — a plain `Publish()` / `event
  Published` bus. Distinct from the busy-signal `ActivityGate Activity`. Publishers
  stay ignorant of who listens; a routine emits to its natural sink only.
- **`DutActivityMirror`** (owned by `EngineHost`) — the *only* component that couples
  actions to the DUT log. On each activity it records a `DutEntry` (key
  `"{Category}:{action}"`, JSON value, type `Object`) into the open interactive run,
  or drops it for the record when none is open (`IProcessService.HasActiveDutRun`).
  It also echoes **jogs** to `LogHub` (they were otherwise unlogged on success);
  section boundaries are already logged by their panels, so those aren't echoed.
- **What's published** — jogs (the *actually executed* move: device / local axis /
  step; superseded rapid presses coalesce) and each correction **section**
  (`started` / `completed` / `declined` / `failed`) with structured result fields
  (U/FAU angles + offsets, hill-climb V-peak + Z travel, Auto-W W-offset, stop
  reasons). Per-step engine measurements are **not** flooded into the record —
  boundaries + results only. HillClimb brackets at the panel's `OnStart` level (not
  its `ClimbCompleted` event, which fires twice for the SLED-handoff continuation).
- **Sagnac is FAAM-specific** — the generic activity/DUT layer never learns about it.
  The Run page installs a signal probe via `EngineHost.SetActivitySignalProbe(...)`
  (reusing `SiphogHillClimbFeedback`'s SAG-PWR read), and `DutActivityMirror` attaches
  the live value (`sagnacV`) to **jog** events only — so the record shows how an
  operator's jog moved the signal. Yields nothing when the SiPhOG isn't streaming.
- **Durability** — `RecordDutValue` only mutates in memory, so `IProcessService`
  gained `FlushDutRun()` (Layer 2). The mirror flushes immediately on section
  boundaries and on a 750 ms debounce for jog bursts. The interactive DUT session is
  guarded by a lock (`_interactiveDutGate`) so the background flush timer can't tear
  the record against a UI-thread `EndDutRun`.

## Config files (`%APPDATA%\Horizon\config\`)

| File | Owner | Contents |
|---|---|---|
| `app_settings.json` | `Horizon.Common` AppSettingsStore | `mm/pixel`, MoveClick mapping, jog velocity presets |
| `jog_transforms.json` | FAAM (Layer 3) | per-device global→local jog matrices (seeded on first run) |
| `faam_teach_points.json` | FAAM | taught process-step positions |

(plus the engine's shared device configs: `io_devices.json`, `motor_devices.json`,
`pneumatic_slides.json`, …)

## Build / run

- Project lives at `src/FAAM.UI/`, added to `Horizon.sln`. x64 only.
- Packages: `MVTec.HalconDotNet-Windows` (native HALCON bundle),
  `SkiaSharp.Views.WPF` (chart views). The app runs without HALCON installed —
  vision degrades gracefully (see `docs` / engine `VisionFacet`).
- Build in Visual Studio (restore NuGet first); set FAAM.UI as the startup project.

## Status / TODO

- **Process steps are Phase 1 (hardcoded)** in `ProcessStepsPanel.RunStepAsync`
  (gantry moves to taught points; gripper `R_Gripper`; UV/dispenser pneumatic
  slides; dispense IO pulse pin TBD). Phase 2 = promote to Layer 2
  `WorkflowDeck`/`PositionStore` + port the Program Editor/Deck pages.
- **Properties editor** (per-step IO names, debounce/wait delays, camera exposure)
  — button present, not yet implemented.
- **Guided teach** (prompt to jog + save when a step's position is missing/0,0,0)
  — guard in place, wizard TODO.
- Jog dynamics, MoveClick signs, and `mm/pixel` need bench calibration.
