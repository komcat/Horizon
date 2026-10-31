# DLAM.UI — Double Lens Active Alignment Machine

DLAM is the third Layer 3 head on the Horizon engine stack: the FAU-attach head
(FAAM.UI) re-cut for the machine FireDog drives today — a gantry camera, **two PI
hexapods** (`hex-left`, `hex-right`), each holding a lens, actively aligned against a
Keithley photocurrent. This document is the migration plan from FireDog
(`..\komcat\FireDog`, C++/ImGui, machine root `D:\ProgramData\FireDog`) to C# under
Horizon, and the record of what has been built so far.

Status (2026-10-30): **Layer 2 ports done** (§5), **shared library extracted**
(`docs/station-ui-extraction.md`), **DLAM.UI head — first cut built** (§8, not yet run on the rig).

## 1. Decisions

| Decision | Choice |
|---|---|
| How DLAM.UI is built | **Extract a shared WPF library** (`Horizon.Station.UI`) from FAAM.UI — shell chrome, login / access, Machine page sections, telemetry, jog rail, Engineer (Gantt) editor, Repository, OEE, Version / update, DUT viewer. FAAM.UI keeps its FAU-specific panels (SiPhOG, `fau.*`, U-correction, FAU pre-align); DLAM.UI is a thin head with its own Run page. No fork. |
| FireDog script editor / recipe editor | **Not migrated.** Production flows are re-authored in the Engineer editor; algorithms inside FireDog's standard Lua scripts become Layer 2 C# routines + blocks (§5), product-specific procedures become `.moon` scripts. |
| Mosaic (large-area capture) | **Skipped.** |
| FireDog alignment procedure (local frame O1, nominals) | **Later** (§6). |
| Waffle trays | **Later** (§6). |
| `uv_advanced.lua` | Converted to a `.moon` script (§5.6). |

## 2. Hardware — almost entirely covered by existing adapters

From `D:\ProgramData\FireDog\config\hardware.json`:

| FireDog device | Horizon | Note |
|---|---|---|
| `gantry-main` ACS (192.168.0.50:701), `hex-left` PI (.10), `hex-right` PI (.30) | `ACSMotionController`, `PIMotionController` | Pivots −15.7 / 0 / 111 for both hexapods (`pivot.local.json`) — FAAM's per-hexapod pivot write-back already handles two. |
| Basler `main_camera` (192.168.1.68), 0.001725 mm/px | `PylonCameraAdapter` | |
| Keithley 2602B, `TCPIP0::192.168.0.111::INSTR`, ch1 + ch2, NPLC 0.1 | `Keithley2602BAdapter` | Adapter fixed 2026-10-29 to FireDog's rule: a `::INSTR` resource opens through NI-VISA (VXI-11; the raw socket on 5025 accepted the link but never answered), connect is confirmed by `*IDN?`, prompts / error echo go off before the first query, every enabled channel is configured, and output ON / OFF and compliance apply to all of them (the source level only to the primary, the others at 0 V — an SMU with its output off reads ~0 A). Verified on the rig: ch1 continuous read at 77 samples/s. Ch2 photocurrent still to verify. |
| UNI-T UDP3305S `PSU_Laser`, `TCPIP0::192.168.0.88::INSTR` | `UnitTUDP3305SAdapter` (VISA) | |
| CCS PD3 `LIGHT1` (192.168.0.2:40001), L1 collimate, L2–L4 R/G/B in group `main_camera_light` | `CcsPd3Adapter` | Map to Horizon's lighting groups / colour call-outs. |
| 4 × Ezi-IO: `IOBottom` (.3), `IOTop` (.5), `IOSwitch` (.6), `IOTower` (.4) | `EziIODevice` | |
| WAGO `wago1` | — | Disabled in FireDog; no adapter needed. |
| Pneumatic slides `UV_Head`, `Dispenser_Head` | `PneumaticSlide` | |
| IO watch: 8 switch → output bindings, each with an indicator LED | `IoWatchFacet` | **Check** indicator-LED support. |
| Safety watch: E-stop `IOSwitch.switch_0` rising | Safety facet | |
| Tower light `IOTower` Green/Yellow/Red/Buzzer | FAAM `TowerLight` | Moves to the shared library. |
| `motion_transforms.json` | `jog_transforms.json` | Same `DeviceId` / `M11…M33` format — copies over as is. |

**Config importer — done** (`Horizon.ConfigTool/FireDogImport.cs`). Menu **Import from
FireDog**, or non-interactive:

```
Horizon.ConfigTool --import-firedog [<FireDog root>] [<config dir>] [--overwrite]
```

It reads `config\hardware.json`, `motion_transforms.json` and `pivot` / `tower_light`
(`.local.json` first), never writes to FireDog's folder, and stages the Horizon files through
Horizon's own loaders: `motor_devices`, `io_devices`, `io_watch`, `pneumatic_slides`,
`safety_watch`, `camera-config`, `smu_devices`, `power_supply_devices`, `lighting_devices`,
`jog_transforms`, `faam_hexapod_pivot`, `faam_tower_light`. Existing files are skipped
unless overwriting is chosen (`--overwrite`), which copies them to `_backup-<stamp>\` first;
the config validator runs afterwards. Reported, not imported: WAGO devices, the camera
exposure, the SMU bias voltage (a program sets it), FireDog's registration `Rotation`
matrices. Run on the DLAM PC on 2026-10-29: 12 files, validator clean.

## 3. Product and machine data

| FireDog | Horizon | Migration |
|---|---|---|
| `products\<p>\*.positions.json` | `PositionStore` (`positions.json`) | convert |
| `config\maps.local.json` | `faam_position_map.json` (the map behind `motion.movePath`) | convert |
| `*.grips.json` (Left Lens on hex-left, …) | `GrippedObjectStore` (per product, keyed by name, `ToolDevice`) | convert + verify |
| `config\device_reg.local.json` | `ToolRegistrationStore` (machine-global, keyed by tool device — two registrations fit) | **re-teach** + verify (FireDog: typed rotation + imprinted tip; different model) |
| `config\needle_cal.local.json` | `CameraNeedleCalibrationStore` | **re-measure** (cheap; the record is the licence to move) |
| `*.vision.json` + `vision\*.pgm` | Vision Profiles | convert circle params; **re-teach** NCC templates |
| `*.alignments.json` | — | later (§6) |
| `*.trays.json` | — | later (§6) |
| `products\employees.json` (3 levels, per-employee plaintext password) | `users.json` (4 levels, per-level PBKDF2 password) | import ids + levels; an Admin sets the level passwords |
| `*.recipes.json` / `*.programs.json` | — | **not migrated** — re-author in the Engineer editor (§4) |
| DUT records, OEE, `data\xcan` ML dataset | — | stay in FireDog's data root as a read-only archive |

## 4. FireDog recipe step → Horizon block

Counts are from the `siphog888` product's 45 recipes.

| FireDog step | Uses | Horizon |
|---|---|---|
| `move_to_position`, `move_to_position_approach` | 111 | `motion.moveToPosition` (+ `approachDistance` / `approachVelocity`) |
| `move_relative`, `set_velocity`, `move_axis_to`, `save_position` | 29 | `motion.moveRelative`, `motion.setVelocity`, `motion.updatePosition*` |
| `move_path` | 27 | **`motion.movePath`** — new (§5.4) |
| `move_lift_off` | 6 | **`motion.liftOff`** — new (§5.4) |
| `parallel`, `group`, `loop`, `if`, `sub_recipe` | 56 | `flow.parallel`, `flow.groupStart/End`, `flow.loopStart/End`, `flow.if…`, `flow.subProgram` |
| `set_output`, `extend_slide` / `retract_slide` | 61 | `io.setOutputByName`, `pneumatic.extend` / `retract` |
| `wait_input` | 1 | **`io.waitInput`** — new (§5.5) |
| `wait_until`, `read_topic` | 5 | **`flow.waitUntil`** — new (§5.5); `signal.read` |
| `delay`, `log`, `prompt`, `prompt_input`, `ctx_set` | 134 | `flow.wait`, `flow.log`, `flow.askUser`, `dut.promptText`, `var.set` |
| `manual_adjust` | 6 | `flow.jogPopup` (+ `motion.readPosition` for FireDog's `ctx_prefix`) |
| `xcan` | 7 | **`scan.xcan`** — new (§5.1) |
| `run_script xcan_two_lens.lua` | — | **`scan.xcanTwoLens`** — new (§5.2) |
| `run_script climb_raster.lua` | — | **`scan.climb`** — new (§5.3) |
| `run_script uv_advanced.lua` | — | `script.run UV_Advanced` (§5.6) |
| `raster_scan` | 5 | `scan.grid` / `fau.gridScan` pattern — decide when re-authoring |
| `keithley_read`, `keithley_bias`, `psu_*`, `cld_*`, `light_color`, `camera_exposure` | 37 | `smu.*` / `signal.read`, `psu.*` (`psu.powerUp`), `laser.*`, `lighting.setLightColor`, `camera.setExposure` |
| `dut_open` / `dut_record` / `dut_close` | 15 | `dut.promptSerial`, `dut.record*`, `dut.close` |
| `capture_view` | 4 | `machine.camera.snap` + `machine.ui.showImage` |
| `move_tool_to_camera`, `tool_travel_to_camera`, `grip_*` | 13 | `tool.toolToCamera`, `object.toCamera` (+ vision profile) |
| `dispense_run`, `needle_cal_run` | 8 | `dispense.runRoutine`, `needle.approach` + Horizon's camera→needle calibration |
| `align_run` / `align_solve` / `align_apply` / `move_local` / `move_to_nominal` / `align_station` | 17 | later (§6) |
| `tray_*`, `move_to_cell`, `move_to_active_cell` | 5 | later (§6) |
| `place_*`, `place_mark`, `nominal_view`, `place_view` | 12 | **dropped** — retired inside FireDog itself |
| `halcon_status` | 1 | dropped (Horizon checks HALCON at use) |

## 5. Layer 2 work — done

All in `Horizon.Workflow`, all `.hwf` program steps (`StepDispatcher`; the ports live in
`Execution/StepDispatcher.FireDogPorts.cs`). **Not yet in `OpCatalog`**, so the Engineer
palette does not offer them — catalog entries + help land with the shared library (§7).

### Shared pieces

- `Scanning/FreshSampler` — FireDog's **fresh-sample gate**: instead of a blind settle,
  wait for N new samples on the feedback source's monotonic `SampleCount` (2 = the second
  is entirely post-move); `settleMs` is the *ceiling*, `settleMinMs` a precautionary floor.
  Works for a streaming source (an SMU `feedbackDevice` descriptor counts publishes) and a
  polled one (a catalog `signal` counts one live read per read).
- `Scanning/ScanAxisMotion` — time-bounded single-axis / pose moves + pose reads; any
  failure throws so the scan unwinds to its fail-safe park.
- Feedback for every scan block: `signal` (catalog key, e.g. `smu.Keithley_2602B.ch1.current`)
  or `feedbackKind` / `feedbackDevice` / `feedbackChannel` / `feedbackValue` /
  `feedbackIntervalMs` (default 20 ms — an SMU stream).
- Signal levels (`exitTarget`, `zoneLow`, `zoneHigh`, `minSignal`, `target`) are entered in
  **`unit`** = `uA` | `nA` | `mA` | `raw`; blank = µA for a current signal (FireDog's
  convention), raw otherwise. Results are written in the signal's own unit (amps).

**Deliberate differences from FireDog** (all scans):

1. **Fail-safe park on STOP.** On a STOP, motion failure or any error the stage returns to
   the best point measured, on a fresh 30 s token — Horizon's active-alignment rule
   (`docs/coding-standards-power-of-10.md`). FireDog left a stopped scan where it stood.
   An exit-threshold hit still stays on the point that hit it.
2. **`settleMinMs` defaults to 50 ms** (FireDog's production value; its script default was
   0). Horizon's move returns when the controller stops reporting motion — there is no PI
   on-target (`qONT`) wait behind it as in FireDog, so the floor stays on until Layer 1
   gains one (§7).
3. No ML scan log (`fd.scanlog` / `data\xcan`). `trace = true` logs the same per-sample CSV
   lines (`xcan,…` / `clmb,…`).

### 5.1 `scan.xcan` — `Xcan/XcanEngine`

Port of `hexapod_align.lua` (FireDog `docs/xcan-scan-design.md`): coordinate descent over a
coarse → fine ladder; per axis a ratchet walk one way, the other unless the peak is
bracketed, then the better point; global-peak recovery after each axis; adaptive
(signal-zoned, equal-log) ladder; stop-and-stay exit threshold; skip-opposite /
direction-memory / sample-reuse trims.

| Param | Default | |
|---|---|---|
| `device` | — | hexapod |
| `axes` | `ZYX` | the SET (`XYZ`, `XY`, …); order is always Z, Y, X |
| `stepsUm` | `10,5,2,1` | ladder (each ≤ 100 µm) |
| `exitTarget` | 0 = off | in `unit` |
| `adaptive`, `zoneLow`, `zoneHigh` | off, 1, 400 | `zoneLow` must equal the hand-off level |
| `freshSamples`, `settleMs`, `settleMinMs` | 2, 300, 50 | |
| `avgCount`, `consecLimit`, `sigDropPct`, `maxTravelUm`, `improvePct` | 3, 3, 15, 200, 1 | travel cap ≤ 1000 µm |
| `minSignal`, `velocity`, `moveTimeoutMs` | 0, 1 mm/s, 10 000 | |
| `skipOpposite`, `dirMemory`, `reuseSample`, `trace` | on, on, on, off | |
| `captureAs` | `xcan` | → `_baseline _best _peak _delta _gain _exitHit _passes` |

**Script form — `tools/dlam/scripts/XCAN.moon`.** The same algorithm on the `machine.*` API,
for running from any script surface: `args` `device` (default `hex-left`), feedback
`smu` / `channel` (default `Keithley_2602B` ch1 through `machine.smu.readCurrent`, a fresh
measurement per read) or a catalog `signal`, levels in µA (`exitUa`, `zoneLowUa`,
`zoneHighUa`, `minSignalUa`), `bias` (default 1 V on the primary channel + outputs ON;
`false` = leave the SMU alone), plus the pass params above. `machine.onAbort` returns the
hexapod to the global peak. `Horizon.Console` › Scripting › **Run XCAN (pick a hexapod)** lists
the PI hexapods and runs it with `device` set (the script ships next to the exe). Checked
offline against a simulated Gaussian peak: lands on the peak in 86 samples (10/5/2/1 µm),
stops and stays at an exit target, and the adaptive ladder skips the coarse rungs.

### 5.2 `scan.xcanTwoLens` — `Xcan/XcanTwoLens`

Port of `xcan_two_lens.lua`: up to four adaptive XCAN passes in a fixed order — **left
coarse, left coarse again, left fine, right coarse** — each exiting at the target; the
sequence ends the moment one reaches it (its exit fired, or a fresh reading ≥ target).
Params: `left` (`hex-left`), `right` (`hex-right`), `target` (required), `zoneLow` (200),
`zoneHigh` (0 = target), `coarseStepsUm` (`10,5,2,1`), `fineStepsUm` (`2,1,0.5`),
`failIfMissed` (off), plus any `scan.xcan` pass param. `captureAs` `xtl` →
`_reached _passes _last _best _live`.

### 5.3 `scan.climb` — `ClimbScan/ClimbScanEngine`

Port of `climb_raster.lua` (FireDog `docs/climb-raster-design.md`) — **not** Horizon's
deprecated `RasterClimbEngine`. Step Z toward the part; probe once in place; while one Z
step alone gains > `zProbePct` skip the plane (at most `maxSkips` in a row), else search
the XY plane (`cross` line scans or `spiral`) and move to its maximum. Stops on target
(`targetPct` of `target`), suspected contact (plane best `contactDropPct` under the peak),
stall, or the Z budget; always parks on the run's best point. A move back to a remembered
point **retreats Z first / advances Z last** (FireDog moved A, B, then Z).

Params: `device`, `target` (required), `targetPct` 85, `zAxis` Z, `planeAxes` `X,Y`, `zDir`
+1, `zStepUm` 1, `zSettleMs` 200, `xyStepUm` 0.5, `xySteps` 6, `planeScan` cross,
`peakDrop` 2, `crossPasses` 1, `ringsStop` 2, `improvePct` 2, `zProbePct` 5, `maxSkips` 5,
`maxZUm` 20 (≤ 200), `maxZSteps` 2000, `contactDropPct` 10, `stallPlanes` 0, `rasterStart`
off, + the measurement params. `captureAs` `climb` → `_baseline _best _final _target
_targetFrac _stop _exitHit _zUm _bestZUm _bestAUm _bestBUm _planes _skipped _points _gain`.

### 5.4 Motion — `motion.liftOff`, `motion.movePath`

- **`motion.liftOff`** — lift `liftMm` (default 2, ≤ 50; 0 = none) along `liftAxis`
  (`X+`, `Z-`, …; blank = PI → X+, ACS → Z−) at the current velocity, then
  `motion.moveToPosition` semantics to `position` (velocity / camera / exposure / approach /
  lighting). Everything is validated before anything moves.
- **`motion.movePath`** — leg-by-leg along the device's position map to `to`, over the
  fewest-hop linked route, confirming arrival after each leg. **Strict start**: `from`
  names the node the device must stand on (checked within `startTolMm`, default 0.01), or
  blank = whichever node it stands on; no implicit move to the start. Approach only on the
  last leg. `PositionMapStore` / `PositionMatch` moved from FAAM.UI to Layer 2
  (`PositionMap/`, + `PositionRoute`); the file is still `faam_position_map.json`.

### 5.5 Waits — `io.waitInput`, `flow.waitUntil`

- **`io.waitInput`** — `device`, `pin` (name or index), `state` (default on), `timeoutMs`
  10 000 (≤ 600 000), `pollMs` 20. Reads the IO device's polled state.
- **`flow.waitUntil`** — exactly one of `signal` (catalog key, read live) or `var` (context
  variable, e.g. set by a parallel lane); `op` `> < >= <= == !=`, `value`, `timeoutMs`
  10 000 (≤ 3 600 000), `pollMs` 50.

### 5.6 `UV_Advanced.moon`

`tools/dlam/scripts/UV_Advanced.moon` — FireDog's `uv_advanced.lua` (two UV cure loops with
fixed-step X/Y drift correction) on the `machine.*` API, configured by the `args` table
(`io`, `device`, `signal`, pulse shape, per-loop pins / counts / waits / drop thresholds).
Copy it into the product's `scripts\`. Differences: `machine.onAbort` switches both UV
outputs OFF and, if a correction was running, returns the hexapod to that correction's start
pose (FireDog left it where it stopped); an n/a reading is "no read succeeded" (a catalog SMU
read is a live query, so FireDog's frozen-stream check has no equivalent); results are
**logged only** — `.moon` has no binding to write program context or DUT values yet (§7).

## 6. To do later

1. **Alignment procedure** (FireDog `alignment_procedure.lua`, `CommandsAlign.cpp`,
   `docs/alignment-procedure-design.md`): two-station taught local frame O1 with a
   span / θ guard, nominals stored local-only, the live-frame licence rules
   (`align_run` / `align_solve` / `align_apply` / `move_local` / `move_to_nominal`).
   Extend Part Alignment (E1/E2 fiducials) or build a sibling module.
2. **Waffle trays** (`docs/waffle-tray-design.md`): a grid from three taught corners,
   `move_to_cell`, the runtime cursor (`tray_next` / `tray_reset` / `tray_mark_used`), used
   cells as product state.

## 7. Remaining phases

| # | Phase | Notes |
|---|---|---|
| 1 | ~~Config importer~~ (done); bring-up of every device | Validates Layer 1 on the DLAM rig (Keithley 2602B two-channel + IO-watch LED checks, §2). First scan runs: `tools/dlam/programs/XCAN_Test_Left.hwf` (one XCAN pass, FireDog's coarse preset) and `XCAN_Test_TwoLens.hwf` (production settings, 6800 µA) — install into a product's `processes\` and run from `Horizon.Workflow.Console` › Process programs. |
| 2 | ~~Extract `Horizon.Station.UI`~~ | Done — slices 1–3 + the Run-page kit (`docs/station-ui-extraction.md`). |
| 3 | DLAM.UI head | **First cut built** (§8): Run page (deck steps, two lens cards with XCAN coarse / fine + climb, two-lens XCAN, Keithley ch1 / ch2 readouts), DUT Data page, `OpCatalog` entries + help for every §5 block, Machine › Settings panel. Open: run it on the rig; the release pipeline (package id `DLAM.UI`, Velopack channel `dlam`, tags `dlam-v*`); needle / dispense on the Run page (today: Engineer › Dispensing and the vision pages). |
| 4 | Product data conversion; re-teach registrations, needle, vision | §3. |
| 5 | Re-author "PROD Tall AUTO V2" in the Engineer editor; run side by side with FireDog | |
| 6 | §6 items | |

Follow-ups found while porting:

- **Layer 1**: an on-target (`qONT`) wait for PI moves, after which the scans'
  `settleMinMs` floor can drop to FireDog's 0.
- **`.moon`**: a `machine.ctx` / `machine.dut.record` binding so scripts can return results.

## 8. DLAM.UI — the head (first cut, 2026-10-30)

`src/DLAM.UI` — a thin head on `Horizon.Station.UI`, same shape as FAAM.UI (`Program.Main` with
the Velopack hook → `App` → `new EngineHost(new DlamHead())` → startup / login / shell). Run it
**instead of** FAAM.UI on this PC (one process owns the SDKs; both read `%APPDATA%\Horizon\config`).

| Piece | What |
|---|---|
| `App` | `StationApp.Title = "DLAM"`, `FullName = "Dual Lens Alignment Machine"` (startup headline), `Mascot` = `FireDogAvatar` (FireDog's flame-tailed corgi, ported from FireDog `Widgets.cpp` `DrawFireDog`), no default tool device (both hexapods hold a lens), log `%APPDATA%\Horizon\logs\dlam-.log`. |
| `DlamHead` | DUT process "Double-lens alignment", default product `DLAM`, no Engineer tabs, no legacy import. |
| Run page (`DlamRunView`) | The shared Run-page kit — `RunStepsPanel` (the product's deck; no built-in fallback steps), `RunCameraPanel`, `RunLogView` — plus `AlignBand`. Camera column width in `dlam_run_layout.json`. |
| `AlignBand` | Keithley ch1 / ch2 photocurrent (read directly while idle, from the SMU stream while something runs); a card per lens (live X Y Z U V W; **XCAN coarse**, **XCAN fine**, **Climb**); **Two-lens XCAN → target**; Stop. Each button runs one Layer 2 block (`scan.xcan` / `scan.climb` / `scan.xcanTwoLens`) as a one-step program, so it is the same code an Engineer program runs, fail-safe park included. Holds the activity gate's alignment side; refused while a program / step runs or a safety flag is up; cancelled by its Stop, the Run page STOP (`EngineHost.StopPressed`) and a safety trip. Writes `align` lines into the open DUT run. |
| Settings | Machine › Settings › **Run page alignment band** — `dlam_align_band.json` (`AlignBandSettings`): lens hexapods (`hex-left` / `hex-right`), SMU (`Keithley_2602B`) + feedback channel, axes, coarse / fine ladders, XCAN exit target, min starting signal (1 µA), two-lens target (6800 µA) + zone low (200), climb target / Z step / Z budget. |
| DUT Data page | The library's generic `DutRecordsView` (serials → runs → recorded values, CSV export of a run). |
| Engineer blocks | `DlamOps` / `DlamOpHelp`: XCAN, XCAN Two Lens, Climb Scan (Scan); Lift Off & Move, Move Along Map (Motion); Wait for Input (IO); Wait Until (Flow). Tuning params left blank = the Layer 2 default. |
| Version | `DlamVersionInfo` target → `<repo>\build\version-info.ps1 -TagPrefix dlam-v`, resource `DLAM.UI.version_info.json`. `<Version>` 0.1.0. |

## 9. Ported FireDog recipes (2026-10-30)

`tools/dlam/firedog_port.py` converts recipes of the FireDog product `siphog888` into Horizon
programs. It reads FireDog read-only, writes the programs + deck for review to
`tools/dlam/programs/PROD_Tall_AUTO_V3/`, and with `--install` installs them into the DLAM product
(`processes\`, `decks\` — made the active deck — `scripts\CaptureView.moon` + `UV_Advanced.moon`),
writes the product's `positions.json` (FireDog's 29 visible positions by name; the 15 hidden
`algn_*` alignment positions skipped) and the three FireDog "ProcessFlow" maps into
`faam_position_map.json` (an existing file is backed up first). Re-runnable; extend `RECIPES` to
port more.

Ported (slot "2 PROD Tall AUTO V3"): **Initial Machine**, **Probe SLED and PIC**, **PP Left - Array**,
**PP Right - Array**, **UV advance1** — deck `PROD Tall AUTO V3`, every entry a Run-page button,
Auto off (the slot's Scan Left 800uA / Scan Two Lens 6800uA, Init SMU and TEC and STEP DISPENSE
are not ported yet, so nothing chains past them).

| FireDog | Horizon |
|---|---|
| `parallel` (children per device run in order) | `flow.parallel`, one branch per device |
| `group` | `flow.groupStart` … `flow.groupEnd` |
| `move_to_position` / `move_lift_off` / `move_path` / `move_relative` / `set_velocity` | `motion.moveToPosition` / `motion.liftOff` / `motion.movePath` / `motion.moveRelative` / `motion.setVelocity`; an exposure override names `main_camera` |
| `move_axis_to` (gantry Z → 30) | `motion.readPosition` (prefix `fd_axis`) + `motion.moveRelative` distance `expr:30 - fd_axis_z` |
| `psu_startup` mode CC | `psu.powerUp` + **`acceptCurrentLimit`** (new block param) |
| `prompt_input` serial + `dut_open` new | `dut.promptSerial` + **`newUnit`** (new block param: never adopt the previous unit's sticky serial; a Run-page Start DUT is still adopted) |
| `read_topic keithley/ch1`, `keithley_read` | `signal.read smu.Keithley_2602B.ch1.current`, `smu.readCurrent` |
| `ctx_set align_*` + `run_script hexapod_align.lua` (+ `dut_record align_best`) | `scan.xcan` (axes XYZ, ladder 1,0.5 µm, settle 150 ms, captureAs `xcan_left` / `xcan_right`) + `dut.recordFromContext` from `…_best` |
| `run_script uv_advanced.lua` | `script.run UV_Advanced` (no args — the defaults equal FireDog's) |
| `capture_view` | `script.run CaptureView` (snap `main_camera` → Run-page snapshot box) |
| `log "… {ctx}"` | `flow.comment` (`flow.log` does not interpolate; the read steps log their values) |
| `cld_*` (disabled in FireDog; no CLD101x on this rig) | disabled `flow.comment` |

Differences: each deck step is its own program run, so a unit's DUT record gets one run entry per
program (all under the serial entered in Probe SLED; UV advance1's `dut.close` ends it). XCAN reads
the Keithley live through the signal catalog (FireDog used the stream). First run: verify the
motion at low speed, as FireDog's own notes ask.
