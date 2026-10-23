# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

- **Solution**: `Horizon.sln` — 24 projects, all `net8.0`/`net8.0-windows`, x64 only. All projects live under `src/`.
- **Claude compiles** — after making code changes, build the solution as **x64 Release**
  (`dotnet build Horizon.sln -c Release -p:Platform=x64`) and fix any errors before reporting
  done. The user still compiles CMake themselves.
- **User tests themselves** — no test projects exist; skip test commands.
- No CI/CD pipelines configured.
- **Runnable entry points** (`OutputType=Exe`/`WinExe`):
  - `Horizon.UI` — the reference operator application (Layer 3, WPF).
  - `FAAM.UI` — a second Layer 3 operator app (the FAU-attach "head"), on the *same* engine stack. Run `Horizon.UI` *or* `FAAM.UI` *or* `Horizon.App`, never two at once — a single process owns the hardware SDKs, and the two UIs share `%APPDATA%\Horizon\config\`. See "Layer 3" below and `docs/faam-ui.md`.
  - `Horizon.App` — the legacy AvalonDock application (see "Legacy stack" below).
  - `Horizon.Console`, `Horizon.Workflow.Console` — Spectre.Console test rigs for Layers 1 & 2.
  - `Horizon.ConfigTool` — Spectre.Console hardware-config authoring utility.
- `src/Horizon.Shell/` is an orphaned directory (no `.csproj`, not in the solution) — ignore it.

## The 3-Layer Architecture

This is the single most important thing to understand. Horizon was split from a
monolithic WPF app into three stacked layers, each in its own assembly, each
depending only on the layer below via interfaces. Design docs: `docs/headless-engine-concept.md`,
`docs/layer1-hardware-engine-design.md`, `docs/layer2-process-module-design.md`,
`docs/layer3-ui-design.md`.

```
Layer 3  Horizon.UI          operator UI        → IProcessService + IHorizonEngine    (active dev)
Layer 2  Horizon.Workflow    product/process    → IHorizonEngine only
Layer 1  Horizon.Engine      hardware engine    → owns all hardware adapters
```

**Strict boundary rule**: a layer references only the layer below, through its
interface. Layer 2 references `Horizon.Engine` + `Horizon.Common` *only* — never
hardware adapters, never `Horizon.Core`/legacy `Horizon.Process`. Layer 3
references `Horizon.Workflow` + `Horizon.Engine` + `Horizon.Common`. Do not add
references that cross or skip layers.

### Layer 1 — Horizon.Engine (hardware engine)

`IHorizonEngine` (`HorizonEngine.cs`) is a headless, UI-free, async host that
owns all hardware. Lifecycle: `InitializeAsync` (load config, build device
objects, no I/O) → `ConnectAllAsync` (open devices, `IProgress<string>`
reporting) → `GetStatusSnapshot` (cached, non-blocking, any-thread) →
`DisposeAsync` (fixed safe teardown order).

Hardware is exposed as **facets** under `Facets/` — one interface + impl + models
trio per domain: Motion, IO, Pneumatic, Measurement, Laser, PowerSupply,
Lighting, Tec, Smu, Camera, Vision (HALCON), Safety. The engine **keeps the
existing adapter assemblies** (`Horizon.Motion`, `Horizon.Camera`, `Horizon.IO`,
etc. with their `Native/` P/Invoke) — only the composition/facet layer was
recreated. The engine is coordinate-only: no named positions, no products.

### Layer 2 — Horizon.Workflow (product/process module)

`IProcessService` (`ProcessService.cs`) is the Layer 2 facade: products, teach
points / named positions (name → coordinates), and program/recipe execution
against `IHorizonEngine`. This is a **clean recreate** — it does *not* reuse the
legacy `ProcessEngine`/`RecipeEngine` or the `.hproc`/`.hrcp` formats; Layer 2
defines its own program/recipe/position formats.

- `Execution/` — `WorkflowRunner`, `WorkflowProgram`, `StepDispatcher`,
  `WorkflowContext`, `ExpressionEvaluator`, `ConditionEvaluator`, `PauseController`.
  Programs are flat `.hwf` JSON, but a **`flow.parallel`** step carries `Branches`
  (per-lane sub-sequences) the runner runs **concurrently** via recursive sub-runs +
  `Task.WhenAll`, fail-fast (a linked token cancels siblings on first failure) — the
  executable form of the Gantt editor's parallel stages. Move ops
  (`motion.moveToPosition` / `moveRelative`) take optional **`MoveOverrides`**
  (velocity / camera / exposure applied at the *start* of the move, so the camera
  settle overlaps travel; `MoveExposure` is the shared apply helper). `moveToPosition`
  also takes an **approach** (`approachDistance` mm at `approachVelocity` mm/s, both > 0):
  `MoveApproach` runs fast to that point short of the target, confirms it stopped there,
  finishes slow, then restores the velocity snapshot. `motion.slowApproach` /
  `SlowApproach` is **deprecated — do not use or build on it**.
  `MotionVelocityLimits` caps velocity by controller type (PI 0.01–10, ACS 0.1–50 mm/s).
- `Execution/DeckRunner` + `DeckStore` — a **deck** is a chain of programs with
  transition conditions between them.
- `Recipe/` — a recreated `.hrcp`-style scripting interpreter (lexer → parser →
  AST → interpreter, `RecipeMethodRegistry`).
- `ProductStore`, `PositionStore`, `ProgramStore` — per-product data I/O. A product is
  a directory `%APPDATA%\Horizon\products\{name}\` (`processes\*.hwf`, `positions.json`,
  `decks\`, `recipes\`, `dut\`, `product.json` metadata via `ProductInfo`); the active
  product is recorded in `products\active.json`. `IProcessService` exposes
  `ActiveProductInfo` + a `ProductChanged` event and refuses to switch mid-run.
  **Product variations** (same process, different positions/edited blocks — each
  product owns an editable copy, not a shared library): `ProductStore.CloneProduct`
  (FAAM Home "Save As…") deep-copies positions, vision profiles, and
  programs/decks/recipes into a new product with a fresh identity and empty DUT
  history (only `dut\` is excluded). The deck editor has its own **Save As**
  (duplicate a deck in-product with fresh deck/entry UIDs so history doesn't fork)
  and **Import** (`ListDecksOf`/`ImportDeck` — pull a deck from another product,
  copying any missing referenced programs). The Run page re-points to the active
  product's deck on `ProductChanged`.
- `IOperatorPrompt` — abstraction for asking the operator a question; the UI
  supplies a WPF implementation, consoles a stdin one.
- `Scanning/` — hexapod beam-alignment scan (coarse-to-fine hill-climb, ported
  from `bobcat_engine`). `HexapodScanEngine` orchestrates; `CoarseToFineStrategy`
  is the algorithm; `IScanFeedbackSource` bridges SMU/measurement/power-supply
  facet events to the algorithm. See `docs/hexapod-scanning.md`.
- **Alignment routines** (FAU-attach, ported from `acs_solution_saa`) — Layer 2
  algorithm objects driven by Layer 3 panels and exposed as workflow steps:
  - `HillClimb/` — `HillClimbEngine` is the coordinate-descent FAU→PIC active
    alignment (Z+ budget metering, optional `TargetSagnacV` early-exit at
    `TargetReachedFraction`). Feedback comes via `IHillClimbFeedback` — the
    engine has no SiPhOG facet, so a Layer 3 head supplies the signal source
    through `IProcessService.SetHillClimbFeedbackProvider`, consumed by the
    `hillclimb.setAnchor/clearAnchor/start/stop` steps in `StepDispatcher`.
  - `UCorrection/`, `FauPreAlign/` — `UCorrectionRoutine` / `FauPreAlignRoutine`
    with their angle/vector calculators; each is its own non-modal
    `IOperatorPrompt` on the Run page (inline Capture/Apply).
- **Power supply (`psu.*`)** — the same op set on all three surfaces: program blocks
  (`StepDispatcher`), `.moon` (`machine.psu.*`) and `.hrcp` (`psu.<dev>.*`), incl.
  `psu.disconnect` (outputs forced OFF first). **`psu.powerUp`** is a verified power-up —
  link check (one reconnect) → I-limit → V → ON → settle (150 ms) → read-back polled (up to
  `VerifyMs`, 2 s) until it shows ON and V within tolerance, else the output is switched back
  **OFF** (fail-safe, also on cancel). `AcceptCurrentLimit` (Run-page panel only) treats a load
  held at the current limit (CC mode) as a successful ON. One shared implementation, `Execution/PsuPowerUp.cs`, behind all three
  surfaces. `IProcessService.IsRunning` (program/deck/recipe in progress) lets Layer 3
  lock manual controls during a run. See `docs/power-supply.md`.
- `Dut/` — `DutStore` for per-DUT (device-under-test) results, keyed by serial. A
  `DutRun` is stamped with the operator + station (`OperatorContext`, set via
  `IProcessService.SetOperator`). Beyond the program-driven path, an **interactive DUT
  session** (`BeginDutRun`/`RecordDutValue`/`EndDutRun`) brackets operator-driven
  processes that don't execute a `WorkflowProgram` (FAAM's hand-clicked Run page).
  **SiPhOG labelled snapshots** (`Dut/SiphogSnapshotStore` + `SiphogSnapshotSampler`): the
  `siphog.saveSnapshot` block / `.moon` `machine.siphog.save(label, windowMs, note)` average the
  six `siphog.*` signals over a window (mean/min/max/std) and append a labelled, operator-stamped
  set to `products\{name}\siphog_data\{serial}.json` (one file per DUT, append-only `sets`,
  schema `horizon.siphog-snapshots/1`; not cloned with the product). Best-effort (non-fatal).
  Each set carries a `runId`/`runStarted`: a run = one pass of the unit, begun each time the serial
  is entered (`dut.promptSerial` / up-front serial / Run-page Start) and ended by `dut.close`,
  Run-page Pass/Fail, serial clear or product switch — so a re-run serial never mixes attempts
  (`SiphogSnapshotFile.Runs()` groups them; pre-tracking sets form one untracked run).
- **Program history / version repository (SQLite)** — `ProgramHistoryDb` writes a
  *machine-global* `%APPDATA%\Horizon\history.db` (`Microsoft.Data.Sqlite`): one
  operator-stamped, numbered snapshot per **commit**, plus normalized `block_revision` /
  `block_param` tables keyed by the block UID. **Save vs Commit is split (git-style)**:
  `SaveProgram` / `SaveDeck` write the working `.hwf` / `.hwd` only; `CommitProgram` /
  `CommitDeck` save *and* record a new version with an optional **note** (the only path
  that bumps `vN`). `RestoreProgramVersion` / `RestoreDeckVersion` (**checkout**) write a
  stored snapshot back as the working copy without adding a version. `ProgramDiff` /
  `DeckDiff` diff two revisions; `IProcessService.QueryHistory` queries saved params by
  UID / key / device / operator. The FAAM **Repository** page is the browse/checkout UI
  (Commit lives on each editor's toolbar). Decks version the same way (keyed by deck
  UID). See `docs/program-history-sqlite-design.md` + `docs/version-repository-design.md`.
- `Oee/` — **station OEE**: `OeeTracker` (`IProcessService.Oee`) records online sessions,
  executions (programs / decks / recipes / scripts bracketed in `ProcessService`; re-entrant
  `BeginProduction` tokens), engineer-declared downtime (`GoDown`/`GoUp`, name-signed, persists
  across restarts) and finished DUT verdicts into machine-global `%APPDATA%\Horizon\oee.db`;
  `OeeCalculator` classifies each 24 h day into Running / Standby (< 30 min gap) / Idle
  (>= 30 min gap, whole gap) / Down / Offline. See `docs/oee-design.md`.
- Operator identity / login lives in Layer 3; Layer 2 only *receives* an
  `OperatorContext`. `users.json` (operator directory) + `AppSettings.StationId` are
  machine-global (`Horizon.Common`). See `docs/product-and-traceability-design.md`.
- `Migration/` — `BobcatRecipeConverter` translates legacy `bobcat_engine`
  recipe JSON into `WorkflowProgram` JSON; `BobcatMigration` is the entry
  point. See `docs/recipe-migration.md` for the Rosetta-stone mapping
  (bobcat *recipe* → Horizon `WorkflowProgram`; bobcat *program* →
  `WorkflowDeck`). Note that `Recipe/` (the `.hrcp` interpreter) is a
  *separate* language — bobcat recipes do NOT target it.
- `DeviceLinkStore` — machine-global `device_links.json` storing spatial
  offsets between motion devices; written by `motion.registerDeviceLink`,
  replayed by `motion.compensateDeviceLink`.
- `Registration/` — **device registration**: a tool on one motion device (the hex-right
  gripper) registered to the camera on another (gantry-main). `ToolRegistrationStore`
  (machine-global `tool_registrations.json`), `ToolRegistrationRoutine` (`Locate` from
  cached states + the two **bounded** moves — refused, never clamped, beyond `MaxMoveMm`),
  exposed on `IProcessService` and as the `tool.cameraToTool` / `tool.toolToCamera` steps.
  The procedure UI is FAAM's Machine › Registration section; `ToolOverlayView` draws the
  live gripper on the Run feed / zoom inset / pop-out. A per-product **gripped object**
  (`GrippedObject`/`GrippedObjectStore`, the FAU held by the gripper, taught on FAAM's Vision
  Alignment page › Gripped FAU tab from two points + outline) rides on the registration and is drawn purple
  by the same overlay. See `docs/device-registration-design.md`.

### Layer 3 — operator applications (Horizon.UI and FAAM.UI)

There are **two** Layer 3 "heads", both WPF, both built on the *same* Layer 1/2
engine stack (reference `Horizon.Workflow` + `Horizon.Engine` + `Horizon.Common`
only). They are independent apps, not a fork — pick one per rig.

#### Horizon.UI (reference operator app)

A greenfield WPF app (`net8.0-windows10.0.19041`), **not** a port of
`Horizon.App` — no AvalonDock, a fixed purpose-built operator layout. MVVM with a
hand-rolled `ObservableObject`/`RelayCommand` (`Mvvm/`).

- `EngineHost` — owns the `IHorizonEngine`/`IProcessService` instances and
  startup/shutdown sequencing (`StartupWindow`, `ShutdownWindow`).
- `ShellWindow` + `DisplayWindow` — main window plus detachable display windows.
- **Thread rule**: the main UI thread must never block. All hardware I/O, frame
  conversion and data production stays on background threads. `Telemetry/`
  (`TelemetryChannel`, `TelemetryStore`, `UiSampler`) is the non-blocking
  pipeline that feeds motion/measurement data to the UI.
- Pages: Machine (hardware status + manual control, composed of `ViewModels/Sections/`),
  Program Editor, Deck (process sequence). Views in `Views/`, view-models in `ViewModels/`.
- Also has the operator login gate + identity chip (`LoginWindow`/`Session`), so its
  program/deck runs are operator-stamped; it does not yet have the Gantt editor or DUT
  Run page (those are FAAM.UI-only so far).

#### FAAM.UI (FAU-attach operator app)

A second Layer 3 head purpose-built for the FAU-attach process — mirrors
Horizon.UI's `EngineHost`/`ShellWindow`/`Telemetry`/`Mvvm` shape with its own
fixed chrome. Full design + status in `docs/faam-ui.md` and `docs/faam-handoff.md`.

- **Startup flow**: `StartupWindow` (connect hardware) → **`LoginWindow`** (level +
  employee number + that level's shared password; `Session` pushes an
  `OperatorContext` — employee, level, machine number, machine credential — to Layer 2
  for traceability) → `ShellWindow`. The shell header has an always-visible product +
  operator·level identity chip and a Logout button.
- **Access gate**: nothing in the shell works until sign-in. `AccessLevel`
  (Operator < Technician < Engineer < Admin, in `Horizon.Common`) is ranked; every
  screen's minimum lives in **`AccessPolicy`** (FAAM.UI), which both the Home tiles and
  `ShellWindow`'s navigation read — the shell re-checks on navigation and evicts on a
  level drop. **STOP and the safety Acknowledge are never gated.** The Machine page's
  **Settings** section is Admin-only and holds the machine number + credential string,
  the employee directory (add/delete), and the per-level passwords (PBKDF2 verifiers in
  `users.json`; a level with none signs in on the employee number alone). A **built-in
  recovery Admin** (`admin` / `admin`, fixed in `UserStore`) is checked before the
  directory and always signs in — it is a known credential, not a secret, and every use
  is logged. See `docs/access-control-design.md`.
- `ShellWindow` — fixed chrome (Home/jog rail/header) around a swappable page host:
  **Home** (launcher + product selector), **Machine** (ported wholesale from
  Horizon.UI), **Run** (the production page: camera feed + crosshair/Zoom2× inset,
  **MoveClick** click-to-jog, process steps panel with Teach/Run toggle, a
  **snapshot box** beside the Zoom2× inset (`SnapshotView`, FAAM's `IImageDisplaySink`
  — a `.moon` script pushes a captured still into it with `machine.ui.showImage`),
  serial-bracketed DUT run with PASS/FAIL + a DUT results viewer, a **PSU panel**
  (`Views/PsuQuickPanel` — green-on-black V/A readouts + per-channel output ON/OFF, CH1
  default with CH2/CH3 add/remove saved in `faam_psu_panel.json`, toggles locked during a
  run; it replaced the hand-operated Dispense panel), log), **Engineer**
  (the Gantt editor below), and **Vision Alignment** — a tabbed page holding **Vision
  Profile** (per-position vision recipe authoring, below), **Gripped FAU** (the
  gripped-object teach) and **Part Alignment** (fiducials E1/E2 + local objects that locate
  the part on the fixture; `part.*` blocks) — the latter two Engineer-gated tabs; plus
  **Repository** (version browse/checkout) and **DUT Data** (Operator-level viewer of the
  per-serial UV signal snapshots `uv1.0`/`uv1.1`/`uv2.0`/`uv2.1_final` that
  `dut.readAllSignals` files: one row per UV run, signal picker, detail grid, trend chart,
  CSV export — `Views/DutDataView` + `DutUvData`; a second **SiPhOG snapshots** tab, `Views/SiphogSnapshotsView`, reviews the `siphog_data\` sets per serial with long-form CSV export, plus six per-parameter trend charts across every DUT — X = real timeline or DUT serial in first-snapshot order, one line per label). A SiPhOG TCP client lives under `Siphog/` (its Skia chart,
  `SiphogChart`, is no longer on the Run page); `MoveClick.cs` holds the shared
  `mm/pixel` jog maths.
- **Vision Profile page** — authors a saved, per-position vision recipe (camera +
  lighting, an ordered pre-process filter chain of up to 3 stages, a detection
  method, and a taught template) and runs it against a live frame. Detection
  methods (HALCON): **NCC** (`find_ncc_model`, rotation-search → returns match
  angle; anchor is X/Y pixel + rotation), **Circle** (threshold → `connection` →
  `select_shape` by circularity → `smallest_circle`), **Line** (threshold → blob →
  `smallest_rectangle2` major axis), **Corner** (threshold → largest blob →
  `gen_contour_region_xld` → `segment_contours_xld` → fit the two longest non-border
  perpendicular edges → intersect for a sub-pixel corner; auto-reports which of the 4
  die corners via the region centroid), **Edge (regression)** (stack N caliper boxes
  along the edge → `gen_measure_rectangle2`/`measure_pos` → one sub-pixel point each →
  robust IRLS line fit rejecting outliers; for ragged/low-contrast edges, returns the
  same line keys plus the tagged sample `Points`). All return a method-agnostic
  `VisionDetectResult` so one result grid and one `vision.runProfile` step serve
  every method. See `docs/vision-profile-design.md`.
- **Engineer = the Gantt timeline editor** (`Views/TimelineEditorView`,
  `ViewModels/Timeline/`): device **lanes** down the left (from the engine facets),
  ordered **stages** across the top; same-stage-different-lane = parallel. It compiles
  to a `WorkflowProgram` (multi-lane stage → `flow.parallel`) and round-trips back,
  reusing `OpCatalog`/`ProcessBlockViewModel` param editing. Each block has a stable
  UID; a dirty marker + a **History** window (revisions/diff + a UID query tab) sit on
  the SQLite history store. *This replaced the old flat `EngineerView` /
  `ProcessEditorViewModel`, which are now unreferenced dead code (pending deletion).*
  See `docs/timeline-sequence-editor-design.md`.
- **Global jog rail** — connected motion devices, X/Y/Z/U/V/W jog pad, velocity +
  step-size presets; jog clicks are in the **global** frame, converted to
  device-local via per-device matrices in `jog_transforms.json` (`JogTransforms.cs`).
- **Run-page process steps are Phase 1 / hardcoded** in `Views/ProcessStepsPanel`
  (`RunStepAsync`) — not yet promoted to a Layer 2 `WorkflowDeck`/`PositionStore`.
- `EngineHost` forces camera **auto-exposure ON** on each camera-connect edge.
- FAAM-owned config in `%APPDATA%\Horizon\config\`: `jog_transforms.json` (jog
  matrices), `faam_teach_points.json` (legacy taught points, migrated into the product),
  `faam_psu_panel.json` (Run-page PSU channels shown, per supply),
  `faam_psu_defaults.json` (per supply/channel V + I-limit the Run-page PSU ON button
  applies via a verified `PsuPowerUp`, default CV 3.3 V / 1.0 A; edited on Machine › Settings),
  `faam_hexapod_pivot.json` (per PI hexapod pivot R/S/T, default -14.52/0/111; the C-887 resets
  it to 0,0,0 at power-down, so `EngineHost` writes it back after connect and after a Reference
  whenever the controller's differs; edited on Machine › Settings),
  `faam_run_layout.json` (Run-page camera-column splitter width),
  `faam_run_durations.json` (per product/program top-level block timings from earlier runs —
  weights the Run-page progress bar by time and estimates blocks that report no progress;
  a `.moon` script reports its own via `machine.progress(pct, msg)`),
  `faam_scan_panel.json` (Run-page **Scanning** panel under the alignment + SiPhOG panels — `Views/ScanningPanel`; it replaced the Raster Climb / Auto W panels, which are **deprecated — do not use or build on** `RasterClimbPanel` / `AutoWPanel` / `RasterClimbEngine` / `AutoWEngine` (`app.* hillclimb` / `autow` now fail with a deprecation message)
  + `ScanPanelStore`: two hex-right pose slots — small Save1/Save2 buttons store the current pose, Move1/Move2
  below move the hexapod there — and four (2×2) buttons that run an assigned script preset or bare `.moon`
  script; captions / poses / scripts edited by right-click or on Machine › Settings),
  `faam_script_presets.json` (`ScriptPresetStore`, Machine › Settings › Script presets: name + `.moon`
  script + argument text + **No exit threshold** flag (adds `exit = false`; the Run-page button turns orange,
  default blue) — one script, several configurations, e.g. the `FAU_PowerClimb` coarse / fine /
  XY-only presets. **Script arguments**: a Lua table literal (outer braces optional) that
  `MoonScriptEngine` evaluates before `machine` is installed — data only — and hands the script as the
  global `args` table; supplied by a preset, the Run Script block's `args` param, or the Script editor's
  Args box; `MoonScriptEngine.ValidateArgs` syntax-checks it on Settings save. **`machine.onAbort(fn)`**:
  on STOP or a script error the engine runs `fn(reason)` once with the machine API re-bound to a fresh
  60 s token — `FAU_PowerClimb` uses it to return the FAU to its global peak),
  `faam_tower_light.json` (IO device + Green/Yellow/Red/Buzzer pins for the tower light, which
  follows the OEE state — `TowerLight.cs`, see `docs/oee-design.md`),
  `faam_hidden_programs.json` (per product, programs hidden from the Engineer page's
  searchable program list — view preference only),
  `faam_position_map.json` (Machine › Motion position map — per product + device, node spots on
  the canvas and undirected links between positions; a node lights green while the device is on it;
  a selected node offers Move / Update; **Follow path** (default on) moves leg by leg along the links,
  fewest hops, confirming arrival after each leg, one arrow per leg; the jog rail's **Move map** tab
  shows the same map view-only);
  plus shared `app_settings.json` (`AppSettings`/`AppSettingsStore` in `Horizon.Common`:
  `mm/pixel`, MoveClick mapping, jog velocity presets, `StationId`) and the engine
  device configs. Operator directory `users.json` and the program-history
  `history.db` live in `%APPDATA%\Horizon\` (machine-global, both heads share them).
  `users.json` carries employee records (id = employee number, `level`) plus
  `levelPasswords`; a file written before levels existed is upgraded on load by
  promoting everyone to Admin, so a new build never locks the station out.
- **OEE page** (Home › OEE, `Views/OeeView`): live state, 24 h timeline, uptime / availability /
  utilization / quality / OEE, downtime log, 7-day table. The **DOWN / UP toggle**
  (`Views/DowntimeBar`, Engineer page + OEE page, Technician level) asks for name + category +
  reason on DOWN, and name + root cause + action taken on UP — one maintenance-log entry in
  `oee.db`; `MaintenanceHistoryWindow` (OEE page) searches / summarises / exports them. `EngineHost` starts/ends the online session and counts `ActivityGate`
  busy (Run-page steps, alignments) as running. The shell header (title · working folder +
  AppData button · clock · `PikaAvatar`) pika follows the OEE state.
- **Version history** (Home › Version tile → `VersionHistoryWindow`): `build/version-info.ps1`
  runs before each FAAM.UI build (target `FaamVersionInfo` in `FAAM.UI.csproj`) and embeds
  `version_info.json` — `<Version>`, git id / branch / `describe`, dirty flag, and the `v*`
  release tags from `deploy/release.ps1` — read by `AppVersionInfo`. Rewritten only on change;
  never fails the build (no git → assembly version only).
- **Theme rule (both heads)**: use the Windows `SystemColors` palette via the
  `App.xaml` brushes (`Bg/Panel/Text/Subtle/Edge/Accent`), not hardcoded dark hex.

### Legacy stack — Horizon.App / Horizon.Core / Horizon.Process

The original monolith: `Horizon.App` (AvalonDock WPF) → `Horizon.Core.AppContext`
(DI container wiring all managers) → `Horizon.Process` (`ProcessEngine`,
`CommandDispatcher`, plugins, `RecipeEngine`). **This stack is not the path
forward and is not refactored.** Per `docs/layer3-ui-design.md` it is acceptable
if Layer 1/2 recreate work breaks it. Run `Horizon.UI` *or* `Horizon.App`, never
both — a single process owns the hardware SDKs. `docs/architecture_layers.md` and
`docs/architecture.md` describe this legacy stack and are stale relative to the
3-layer split. Touch the legacy stack only when explicitly asked.

## Hardware Adapter Assemblies (shared by all layers' engines)

| Domain | Interface | Adapters | SDK |
|---|---|---|---|
| Camera | ICameraHardware | PylonCameraAdapter, IDSPeakCameraAdapter, UEyeCameraAdapter | Basler Pylon 8, IDS Peak, IDS uEye |
| Motion | IMotionController | ACSMotionController, PIMotionController | ACS.SPiiPlusNET6 (managed, TCP 701), PI_GCS2_DLL_x64 (P/Invoke, TCP 50000) |
| IO | IIODevice | EziIODevice | EziMOTIONPlusE.dll (P/Invoke) |
| Pneumatic | IPneumaticSlide | PneumaticSlide | Uses IIODevice output pins |
| Current Measurement | ICurrentMeasurementDevice | Keithley6482Adapter | visa64.dll P/Invoke (NI-VISA) |
| Laser Controller | ILaserControllerDevice | CLD101xAdapter | Thorlabs CLD101x SCPI over USB |
| Power Supply | IPowerSupplyDevice | KeysightPowerSupplyAdapter, UnitTUDP3305SAdapter | Keysight E36xxx (LXI TCP); UNI-T UDP3305S-E (LAN raw SCPI socket, or VISA USBTMC / VXI-11 — `Transport/ScpiTransport.cs`) |
| Lighting | ILightingDevice | CcsPd3Adapter | CCS PD3 LED controller |
| TEC | ITecDevice | Ted4015Adapter | Thorlabs TED4015 standalone TEC |
| SMU | ISmuDevice | Keithley2400Adapter, Keithley2602BAdapter | Keithley SourceMeter via VISA |

PowerSupply / Lighting / Tec / Smu are newer domains; each follows the same
shape (`Adapters/`, `Interfaces/`, `Models/`, optional `Native/VisaNative.cs`, a
`Manager`) and references `Horizon.Config` for its TCP/connection config types.
`Horizon.Interlock` was extracted out of `Horizon.Core` into its own assembly.

### Fastech Ezi-IO Register Layout

Standalone Ezi-IO modules use a **combined I/O register**: bits `[0..InputCount-1]`
= inputs, bits `[InputCount..InputCount+OutputCount-1]` = outputs. SetOutput/
ReadAllOutputs in `EziIODevice` handle the bit offset automatically. Motor IO
(`FAS_SetIOOutput`) does not use this offset.

## Config System

`ConfigLoader.LoadJson<T>(dir, name)` (`Horizon.Common`) with `.local.json`
override — System.Text.Json, case-insensitive, comments + trailing commas
allowed, camelCase `JsonStringEnumConverter`. Config dir: `%APPDATA%\Horizon\config\`.

Per-file model records live in their domain projects (one source of truth shared
by engine, legacy app, and ConfigTool): `io_devices.json` → `IODeviceConfig`,
`motor_devices.json` → `MotionDevice`, `pneumatic_slides.json` → `PneumaticConfig`,
`safety_watch.json` → `SafetyWatchConfig`, `motion_interlocks.json` → `InterlockConfig`,
`current_measurement_devices.json`, `laser_controller_devices.json`, plus per-domain
files for the newer hardware. Camera config: `%APPDATA%\Horizon\camera-config.json`.

`Horizon.ConfigTool` is preset-driven console tooling for authoring these files
with cross-reference validation (e.g. a pneumatic slide referencing a
nonexistent IO pin). Authoring-side only — not part of the runtime stack.

## Conventions

- Nullable enabled, implicit usings; `AllowUnsafeBlocks` in UI/App only.
- Thread safety: `ConcurrentDictionary` for device collections, `lock` for
  hardware access, `PeriodicTimer` for async polling.
- Engine API is **async/Task-based** throughout.
- Error enums per domain plus unified `ErrorCode` (hex-categorized) and
  `Result<T>`/`Result` in `Horizon.Common`.
- Pin/device names use `StringComparer.OrdinalIgnoreCase`.
- Native DLLs (PI, Fastech, VISA, uEye) copied to output via
  `<None Include="..." CopyToOutputDirectory="PreserveNewest">`.
- VISA instruments share a `Native/VisaNative.cs` P/Invoke pattern.
- Logging: `ILogSink` / `Logging.cs` interfaces; Serilog 4.2.0 in host projects.
- NuGet: AvalonDock 4.72.1 (legacy App only), SkiaSharp.Views.WPF 3.116.1,
  Spectre.Console 0.49.1 (console hosts), `Microsoft.Data.Sqlite` 8.0.10 (Layer 2
  program-history store; full package bundles the native engine for the executables),
  MVTec HALCON 24.05 — `MVTec.HalconDotNet` managed wrapper in library projects,
  `MVTec.HalconDotNet-Windows` native-bundling package in executable projects.
  HALCON 24.05 is x64-only.

### Mission-critical coding standard (enforced at build)

Because Horizon drives motion hardware in closed loops against live sensor
feedback, the repo holds code to a NASA/JPL "Power of 10" adaptation, wired via
root `Directory.Build.props` (`AnalysisLevel=latest`, `EnforceCodeStyleInBuild`)
+ `.editorconfig`. **A subset of analyzer rules are build-breaking errors**, so
write code that won't trip them:

- **No unbounded recursion / no statically-infinite loops** (`S2190`, `S2189` =
  error). A `while(true)` that the analyzer can't prove exits will **fail the
  build**. Long-lived pumps must exit on a cancellation token; if a construct is
  genuinely unbounded-by-design, wrap it in a justified
  `#pragma warning disable S2189` … `restore` with a one-line reason.
- **Every hardware `await` is time-bounded** — pass a timeout/`CancellationToken`;
  `CA2016`/`CA2247` (token forwarding) = error.
- Quality rules (`S138`/`S1541`/`S3776` complexity, `IDE0059`, `CA1806`, `S2201`)
  are **warnings** today, ratcheted to error per-assembly as each is cleaned — do
  not introduce new ones. There is intentionally **no** blanket
  `TreatWarningsAsErrors` (brownfield solution still builds).
- Active-alignment code (`HillClimbEngine`, `CoarseToFineStrategy`, FAU pre-align /
  U-correction) has extra review rules the tools can't check: validate sensor
  inputs at the boundary, and **fail safe** — on any abort (cancel/timeout/error)
  return the stage to the last known-good position. See
  `docs/coding-standards-power-of-10.md`.

## Documentation

- 3-layer design: `docs/headless-engine-concept.md`, `docs/layer1-*.md`,
  `docs/layer2-*.md`, `docs/layer3-ui-design.md`, `docs/config-tool-design.md`.
- Legacy per-module docs in `docs/modules/` and `docs/architecture*.md` describe
  the `Horizon.App`/`Core`/`Process` stack — accurate for legacy code only.
- Trainer design: `docs/position-accuracy-trainer.md`,
  `docs/multi-device-trainer-design.md`.
- Hexapod beam-alignment scan: `docs/hexapod-scanning.md`.
- Bobcat → Horizon recipe migration: `docs/recipe-migration.md`.
- FAAM.UI (second Layer 3 head): `docs/faam-ui.md`, `docs/faam-handoff.md`.
- SMU streaming: `docs/smu-streaming-design.md`.
- Power supply (UDP3305S-E LAN setup, `psu.*` ops, verified `psu.powerUp`, FAAM PSU panel): `docs/power-supply.md`.
- Product encapsulation + operator login + DUT traceability: `docs/product-and-traceability-design.md`.
- Access levels / sign-in gate + machine identity on the DUT record: `docs/access-control-design.md`.
- Gantt timeline editor + `flow.parallel`: `docs/timeline-sequence-editor-design.md`.
- Program history → SQLite: `docs/program-history-sqlite-design.md`.
- Program / deck version repository (commit notes + checkout): `docs/version-repository-design.md`.
- Vision Profile page (per-position vision recipes): `docs/vision-profile-design.md`.
- Device registration (hex-right gripper ↔ gantry camera; `tool.cameraToTool` / `tool.toolToCamera` blocks + live overlay): `docs/device-registration-design.md`.
- Run-page utility-app launcher (phone-style springboard + L2→L3 app facet): `docs/utility-app-launcher-design.md`.
- Engineer command help (palette help panel + `OpCatalogHelp` authoring): `docs/engineer-command-help.md`.
- Station OEE (uptime / downtime / utilization, DOWN-UP toggle, OEE page): `docs/oee-design.md`.
- Mission-critical coding standard (Power of 10): `docs/coding-standards-power-of-10.md`.
