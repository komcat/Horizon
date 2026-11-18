# FAAM.UI v0.7

Feature release of **FAAM.UI**, the FAU-attach operator application on the Horizon
3-layer engine stack (Layer 1 `Horizon.Engine` → Layer 2 `Horizon.Workflow` → Layer 3
`FAAM.UI`). Snapshot of `master` at the tag, published self-contained for `win-x64`.
Internal engineering pre-release for rig deployment.

This page lists what changed since
[v0.6.2](https://github.com/komcat/Horizon/releases/tag/v0.6.2); see the
[v0.6](https://github.com/komcat/Horizon/releases/tag/v0.6) notes for the assets and the
self-updating install.

## What's new since v0.6.2

### Engineer page: free-canvas sequence editor
The lane × stage grid is replaced by a canvas. **The program file format (`.hwf`) is
unchanged** — existing programs open as they are and are laid out top to bottom.

- Every program has a **Start** node. Blocks are dragged anywhere and joined by **arrows**;
  the chain of arrows from Start is the execution order (one arrow in, one out, no loops).
  Stage rows are dashed guide lines only. Blocks side by side do **not** run in parallel.
- A **parallel group** is one envelope whose lanes run at the same time (saved as
  `flow.parallel`, as before).
- Blocks not connected to Start are shown faded, are skipped by Run, and **block Save /
  Commit** until they are linked or deleted.
- Arrows: drag from a block's port, or use the **Arrow** button (click the start block, then
  the end block). Right-click an arrow to remove it.
- **Run Selected**, **Run From** (the selected step to the end), **Pause / Resume** (the
  current block finishes, then the run waits), zoom (− / +, Ctrl + wheel), **Tidy**.
- **New column** from a block's right-click menu: the arrow becomes an E1 / E2 marker pair
  (three columns at most).
- **Folding:** an If / Loop Start / Group Start block folds the blocks up to its closer
  behind it. Folded blocks still run.
- Every block carries a **category icon** (Motion, IO, Vision, Flow, …).
- Block positions, columns and folds are a view preference in
  `program_canvas_layout.json`; they are not part of the program.
- Saving a program whose name contains `\ / : * ? " < > |` is refused with a message
  (it used to throw).
- Log panel: **Copy** puts the whole log on the clipboard.

### Detect Object block (`vision.detectObject`)
- A self-contained detect block: it keeps its own camera position (Go / Teach here / Clear)
  and its own vision recipe (**Vision helper…**). Results go to `<prefix>_found / _x / _y /
  _p / _score …`; X / Y in gantry-main mm, P = 0° up on the image, clockwise positive.
- Optional operator mark when detection fails, optional guarded move to the result
  (refused, never shortened, over the guard; hard cap 5 mm) and offset.
- The result is drawn on the live views for 20 s: the outline of what was matched, a
  cross, the angle pointer and X / Y / P.
- The recipe's exposure is written **before** the move to the block position.
- New detection method **Ray line (black / white)**: parallel rays cross the Search ROI
  from one side, each stops at its first white→black / black→white change, and a line is
  fitted through the points with outliers rejected. For a binary-threshold image.
- **NCC template:** the TEMPLATE box now shows the *filtered* crop the model is trained on
  (tick box for the raw picture), and warns when the filters or Angle ± have changed since
  the template was created — press Create again.

### Beacons
- **Plant Beacon Here** / **Remove Beacon**: virtual markers at the camera centre, drawn
  on the live views as a small +. In memory only, up to 10, 30 minutes each.
- **Draw Beacon Line**: a dashed construction line between two beacons, measured into
  `<prefix>_length / _angle / _cx / _cy / _x1 … _y2`.
- **Camera to Beacon**: centres the camera on a beacon (X / Y only; refused over the
  guard, default 10 mm, at most 50 mm).
- **Register Gripped Object**: a marker that rides on the tool (hex-right), planted at the
  camera position or at a Detect Object result, with an optional outline snapshot.

### Motion
- **ACS adaptive motion profile:** every velocity set also writes ACC / DEC / JERK / KDEC
  derived from it (default ratios 10 / 10 / 2, per device). Machine › Motion › **ACS
  profile** edits the ratios and shows the live values.
- ACS coordinated multi-axis moves.
- Motion inspector: a per-row **Go**; the editor edits recall settings only.
- Product **Save As** copies the position map.

### Other
- **Machine › Vision license:** diagnoses why HALCON is unavailable (SDK, license file,
  dongle).
- Needle calibration and dispensing ported from FireDog, with a motion diagram.
- Scanning panel: **W opt** button.
- Registration panel: the axis map is hidden and "Move to pose" removed.
- Keithley 2602B: VISA for `::INSTR`, both channels.

### Deprecated
- **Part Alignment** (Vision Alignment tab): the tab is disabled and its saved objects are
  no longer drawn on the Run feed or the camera pop-out. Saved data and the `part.*`
  blocks are untouched. Use Detect Object instead.

## Upgrading a station from v0.6.x

- **Home › Version › Update…** (Engineer). No migrations; `%APPDATA%\Horizon` is not
  touched.
- Open each program once on the Engineer page and check the sequence reads as expected
  before running it in production.
- **Check `mm / pixel`** (Machine › Settings). It converts image pixels to millimetres for
  Detect Object, MoveClick and the beacons; a wrong value shows as markers drifting off a
  feature as the camera moves away from it.

## Known limitations

- The canvas editor, Detect Object, the beacon blocks and Ray line are new in this release
  and have had bench use on one station only.
- Pause inside a parallel group, and Run From starting inside an If branch, have not been
  verified.
- Pixel → millimetre conversion assumes the camera is square to the gantry axes; a
  detection far from the image centre carries a small cross-axis error. Use "Move camera to
  the result" and detect again where accuracy matters.
- Otherwise unchanged from [v0.6](https://github.com/komcat/Horizon/releases/tag/v0.6).
