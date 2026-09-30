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
  a swappable page host: **Home**, **Machine**, **Run**, **Engineer**, **Vision
  Alignment**, **Script**, **Calibration**, **Repository**, **DUT Data**.
- Theming: Windows **SystemColors** palette (`App.xaml` brushes `Bg/Panel/Text/
  Subtle/Edge/Accent` + fixed status/video colours) — see `faam-ui` memory / the
  same scheme is mirrored in Horizon.UI.

## Pages

- **Home** — launcher tiles (RUN / Engineer / Machine / Vision Alignment / Script /
  Calibration / Repository / DUT Data) plus the product selector.
- **Machine** — the Horizon.UI Machine page ported wholesale (section rail:
  Status, Motion, IO, Pneumatic, Laser, TEC, Power supply, Measurement, SMU,
  Camera, **Registration** (Engineer), **Settings** (Admin)). Engine status + Reconnect
  live in the shell top bar. Registration registers the hex-right gripper to the
  gantry camera — see `docs/device-registration-design.md`.
- **Sign-in gate** — nothing in the shell works until an operator signs in with
  **level + employee number + level password**. Screens are gated by ranked
  `AccessLevel` through `AccessPolicy` (Operator: Run, DUT Data · Technician: + Machine, Vision
  Alignment · Engineer: + Engineer, Repository, Script, Calibration, the Gripped FAU tab · Admin: + the
  Settings section). The jog rail locks with the station, except **STOP**, which stays
  live at every level. See `docs/product-and-traceability-design.md`.
- **Run** — the production page:
  - **Camera feed** — aspect-fit (`Stretch="Uniform"`, capped at the camera's
    1280×1024): the whole frame is letterboxed into the viewport, so nothing is
    cropped and the overlay crosshair sits on the frame's centre. Crosshair + centre
    ROI, **Zoom2×** inset (centre 200×200 px, also aspect-fit), a security-camera
    thumbnail wall. Every source-px ↔ display-px conversion on the main feed
    (MoveClick, ROI box, vision-detection overlay) goes through
    `MoveClick.DisplayScale(..., cover: false)` — the flag must match the XAML
    stretch or jogs are silently mis-scaled.
  - **Layout** — left column (process steps + alignment utilities) takes the
    remaining width with the **Log** beneath it; the camera column is `Auto`-sized to
    the aspect-correct feed and spans both rows, so it runs full height beside the log.
  - **Snapshot box** — the panel beside the Zoom2× inset (it replaced the SiPhOG SAG /
    target chart, which the operators didn't use). `Views/SnapshotView` is FAAM's
    implementation of the Layer 2 `IImageDisplaySink` seam, registered by `RunView` via
    `IProcessService.SetImageDisplaySink`, so a `.moon` script can push a still into it
    with `machine.ui.showImage(path [, caption])`. The picture is decoded
    `OnLoad` + frozen off the UI thread, so the capture file is released immediately.
    Used by the `capture_snapshot` script that a `script.run` step fires at the Serial
    Number position, just before `dut.promptSerial` — the operator sees the unit the
    serial they are typing belongs to.
  - **MoveClick** — arm the toggle, click the feed/zoom to measure mm from the
    crosshair centre (`mm/pixel`) and jog `gantry-main` (coordinated X+Y).
  - **Process steps panel** (left) — FAU-attach steps, process timer, STOP,
    **Teach/Run** toggle, Properties button.
  - **Power supply panel** (top of the utilities column, `Views/PsuQuickPanel`; replaced
    the hand-operated Dispense panel) — live V / A per channel in a green-on-black
    readout plus an output ON/OFF toggle. CH1 always shown; CH2/CH3 added with `+ CHn`
    and removed with `×` (not while ON), saved in `faam_psu_panel.json`. Toggles lock
    while a step / alignment / scan or a program / deck run is in progress. See
    `docs/power-supply.md`.
  - **Motion Inspector** — right-click a step button → *Inspect Motion*. Fills the
    camera column with the named-position moves **that step's program actually
    performs, in run order** (walking the `.hwf`: top-level `motion.moveToPosition` /
    `motion.parallelMove`, `flow.parallel` branches, and `flow.subProgram` children;
    disabled steps skipped) — not every position the product knows. Per row: the
    velocity the move will use (step override, else the point's), taught X/Y/Z/U/V/W,
    camera + exposure, and a `..` editor.
    **Step forward / Step back** drive the machine one move at a time through that
    sequence — the cursor only advances when the move succeeds, so a bad point is
    found by watching the machine execute. Moves run under the same guards as a
    process step (blocked by alignment-busy / a raised safety flag, held on the
    process side of the activity gate, cancelled by STOP and by a safety trip).
    A green **At** dot marks every row whose device is standing on that taught point
    right now (±0.01 on each axis the device owns), and the cursor snaps to the first
    match on open, so the inspector knows which step the machine is on. The active row
    carries an **Update** button that re-teaches that point from the live pose (keeping
    velocity / camera / exposure and the axes the device doesn't own) — teach the
    sequence in place, step by step. Stepping onto an **untaught** point moves the
    cursor but not the machine (there is nowhere to go): jog there, press Update, and
    carry on — that is how a fresh product gets taught in program order.
  - **Log** — engine + process/jog log lines.
- **DUT Data** (`Views/DutDataView`, Operator) — the UV-cure signal snapshots of every
  unit of the active product. The UV programs' `dut.readAllSignals` steps file a full
  SiPhOG read-out (`siphog.sagPwrV`, `targetSagPwrV`, `photoCurrentUa`, `sledCurrentMa`,
  `sledTempC`, `tecCurrentMa`) under the dataset keys **`uv1.0`** (before cure 1),
  **`uv1.1`** (after cure 1), **`uv2.0`** / **`uv2.1_final`** (around cure 2) as
  `Type=Object` entries in the run's `dut\{serial}.json`. `DutUvData` extracts them
  (ignoring the jog-mirror / log entries in the same run); the page loads every record
  through `IProcessService.ListDutRecords` / `GetDutRecord` on a background thread with
  progress in the status line (cancelled on leaving, reloaded on product switch). The
  **overview** grid is one row per UV run, newest first (a reworked serial appears once per
  run), showing the picked **Signal** at each snapshot plus the `uv2.1_final − uv1.0`
  delta, with a serial **filter** and range presets (All · last 7 days · last 30 days ·
  YTD · last 30 runs · last 100 runs, combined with the filter); selecting a row lists that run's snapshots with every
  signal in the **detail** grid, and the `DutTrendChart` (static SkiaSharp, categorical X)
  trends the picked signal across the visible rows with the selected run marked.
  **Export CSV** writes the visible rows with every snapshot × every signal. The Run
  header's older *Results* window (run metadata only) is unchanged.

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
| `faam_psu_panel.json` | FAAM `PsuQuickPanel` | Run-page PSU channels shown, per supply (`{ "UDP3305S": [1, 3] }`) |

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
