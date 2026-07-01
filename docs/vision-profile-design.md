# Vision Profile — design

A **vision profile** is a saved, per-position vision recipe: which camera + lighting
to use, a pre-process filter, a detection method, and a taught template. It is
authored on the FAAM.UI **Vision Profile** page (launched from the Home tile) and
runs against a live camera frame to locate a feature.

The page offers a **detection-method selector** — **Halcon NCC**, **Circle** and **Line** —
each fed by an **ordered pre-process filter chain** (up to 3 stages, e.g. binary → canny)
and an optional **search ROI** that restricts detection to a region. Filter kinds: **binary
threshold**, **gaussian** (3×3 / 5×5) and **Canny edge** (`edges_image`). Results are shown
as a **key→value grid** (centre x/y, score, method-specific fields) plus a status line; the
second matched-crop panel is still stubbed.

- **NCC** — `find_ncc_model` on the (filtered, ROI-reduced) image; needs a taught template.
  The model is built with a rotation search range (`NccAngleRange`±°, default 180 → full
  circle; 0 = translation only), so it returns the match **angle**. The anchor is a frame —
  X/Y pixel **plus a rotation** (`TemplateRef.Rotation`, default 0, set by a slider and shown
  as axes on the template); the reported `angle` = match angle + anchor rotation. Reports
  x/y, anchorX/Y (taught offset applied), angle, score.
- **Circle** — threshold → `connection` → `select_shape` by circularity → `smallest_circle`;
  picks the qualifying circle closest to the search centre; reports x/y, radius, score.
  Params: min/max radius, min circularity.
- **Line** — sub-pixel Canny (`edges_sub_pix`) → keep contours ≥ min length → `fit_line_contour_xld`
  (tukey) → among the fitted edges pick the one matching **direction + polarity** by the configured
  **scan order** (first / last along the scan, or closest to the ROI centre); reports x/y, endpoints
  x1/y1/x2/y2, angle, length. Params: min length, direction, polarity, scan order. The **angle is
  normalized into Q1 `[0,180)`** (horizontal = 0°, vertical = 90°) — a line has no direction and
  HALCON's endpoint order is arbitrary, so near-vertical edges no longer flip between +90 and −90.

All three return a method-agnostic `VisionDetectResult` (ordered `VisionResultValue` list +
crop), so one grid, one `vision.runProfile` step, one `machine.vision.runProfile` Lua call and one
Run-page overlay serve every method.

## Layering

Like the rest of Horizon, the feature is split across the three layers:

```
Layer 3  FAAM.UI VisionProfileView      page UI + ROI picker window
Layer 2  Horizon.Workflow               VisionProfileStore + history + IProcessService API
Layer 1  Horizon.Engine VisionFacet     HALCON: filters, NCC create/find, model persistence
```

- **Live feed, exposure, lighting** are pure hardware and go Layer 3 → engine facets
  directly (`Engine.Camera`, `Engine.Lighting`), exactly as the Run page does.
- **Persistence, template capture and detection** carry product/operator state, so they
  go through Layer 2 (`IProcessService`).

## Layer 1 — `VisionFacet` (HALCON)

New engine-owned types (`Facets/VisionModels.cs`):

- `VisionFilterKind { None, BinaryThreshold, Gaussian, CannyEdge }`
- `VisionFilter(Kind, ThresholdMin, ThresholdMax, GaussMask, Alpha)` — filters chain in
  order; the facet ops take an `IReadOnlyList<VisionFilter>`
- `RoiSpec(X, Y, Width, Height)` — source pixels
- `NccMatch(CenterX, CenterY, Angle, Score, AnchorX, AnchorY)`
- `NccCrop(Width, Height, Channels, Data)` — raw bytes for UI display
- `NccFindResult(Match, Crop)`, `NccTemplateInfo(Width, Height, AnchorX, AnchorY)`

New `IVisionFacet` operations (all reuse the existing `RunFrameAsync` lock + background
+ `ToHImage`/`ToGray` helpers, and degrade to `Fail(NotSupported)` when HALCON is absent):

- `ApplyFilterAsync(frame, filter)` → `NccCrop` — `gauss_image` / `threshold`+`region_to_bin`,
  returned as a grayscale preview.
- `CreateNccModelAsync(frame, roi, preprocess, modelFilePath, templatePngPath)` —
  crops the ROI from the (filtered) image, `create_ncc_model` (translation only,
  `use_polarity`), `write_ncc_model` to `modelFilePath`, and `write_image` the original
  crop to `templatePngPath`. Anchor defaults to the ROI centre.
- `FindNccModelAsync(frame, modelFilePath, preprocess, minScore, cropSize)` —
  `read_ncc_model` + `find_ncc_model` (all matches), returns the match **closest to the
  frame centre**, with a `cropSize`-px crop around it.

## Layer 2 — store, history, service API

### On-disk layout (per product)

```
%APPDATA%\Horizon\products\{product}\vision\
├── {profile}.hvp                 # VisionProfile JSON
├── active.json                   # last-loaded profile
└── templates\{templateUid}\
    ├── template.ncm              # HALCON NCC model
    └── template.png              # cropped template image
```

`VisionProfileStore` mirrors `DeckStore`: `List / Load / Save / Delete`, `active.json`
tracking, plus template-binary helpers (`NewTemplateUid`, `EnsureTemplateDir`,
`ModelPath`, `ImagePath`, `DeleteTemplate`).

### Model (`VisionProfile.cs`)

`VisionProfile` carries a stable `Uid` (preserved across renames), `Name`, `Device`
(`gantry-main`), `PositionName`, camera + exposure, lighting device + intensity, the
`Preprocess` filter, `DetectionMethod` (`ncc`), `MinScore`, and an optional
`TemplateRef`. `TemplateRef` holds the template `Uid`, `Roi`, a template-local anchor
(default centre) and the template `Width`/`Height`. **Retaking the template mints a new
`Uid`**; the superseded template directory is pruned on the next save.

### History / traceability (SQLite)

`ProgramHistoryDb` gains a `vision_version` table (mirroring `program_version`/`deck_version`)
keyed by `(product, profile_uid, version)` with the operator/station stamp, the
`template_uid`, and the full `snapshot_json`:

```sql
CREATE TABLE vision_version (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    product TEXT, profile_uid TEXT, profile_name TEXT, version INTEGER,
    saved_utc TEXT, operator_id TEXT, operator_name TEXT, station_id TEXT,
    template_uid TEXT, snapshot_json TEXT,
    UNIQUE(product, profile_uid, version));
```

`AppendVisionProfile` writes one operator-stamped revision per `SaveVisionProfile`;
`LoadVisionHistory(product, profileUid)` reads them back (newest last). Same machine-global
`history.db` the program/deck history use.

### `IProcessService` additions

`ListVisionProfiles`, `ActiveVisionProfile`, `LoadVisionProfile`, `SaveVisionProfile`
(assigns a `vp-…` UID, prunes the old template, appends history), `DeleteVisionProfile`,
`GetVisionProfileHistory`, `GetTemplateImagePath`, `CaptureTemplateAsync` and
`RunVisionProfileAsync`. The position combo reuses the existing `GetPositionNames` +
`MoveToNamedPositionAsync`.

`RunVisionProfileAsync` applies the taught anchor offset (`anchor − template-centre`) to
the match centre, so the reported anchor point is in frame coordinates.

## Layer 3 — Vision Profile page

`VisionProfileView` (code-behind, like the Run page) + `RoiSelectWindow`:

- **Toolbar** — position combo + **Move to**; profile combo + **New / Load / Save / History**.
- **Main image** — live feed (newest-frame coalesced render, the Run page's
  `WriteableBitmap` blit) with a centre crosshair; **Camera** combo, **Live** toggle,
  **Single frame**; exposure (auto + µs) and lighting (device + intensity) appliers.
- **Pre-process** — filter kind + params, **Preview** renders the filtered image.
- **Template** — **Create** opens `RoiSelectWindow` (drag a free rectangle; display px →
  source px), **Set anchor** arms click-to-set-anchor on the template, **Clear**; **Detect**
  runs the profile and fills the **Matched crop** + **Results** grid.
- **Live-feed overlay** (after Detect, toggled by the **Overlay** checkbox): a translucent
  yellow **filter-edges** layer (the filtered foreground, an `OverlayImage` with the same
  Uniform stretch so it aligns pixel-for-pixel), the **search ROI** rectangle, and the
  **detection geometry** (line / circle / NCC box + anchor dot) drawn on an `OverlayCanvas`
  via source→display mapping (re-mapped on resize).

The page acquires/releases its camera grab lease on Live toggle and on page
activate/deactivate (ref-counted in the camera facet, so it coexists with the Run page
and camera pop-outs).

## Running a profile from a WorkflowProgram

`StepDispatcher` exposes a **`vision.runProfile`** op (catalogued under *Vision*) so a
`.hwf` program / deck can run a saved profile against its camera's latest frame:

| Param | Meaning |
|---|---|
| `profile` | profile name to run |
| `captureAs` | context-var prefix (default `vision`) |
| `requireMatch` | if true, a no-match fails the step (default: branchable) |

On a detection it sets `{captureAs}_found=1` plus one var per returned value
(`{captureAs}_{key}`, e.g. `_x`, `_y`, `_score`, `_radius`, `_angle` — whatever the method
returns); on no detection it sets `_found=0` and (unless `requireMatch`) returns success —
so recipes branch with `flow.if` on `{captureAs}_found`, mirroring `scan.hexapod`. The
camera must already be grabbing (e.g. on the Run page). Backed by
`IProcessService.RunVisionProfileByNameAsync` → the facet's cameraId-based finders
(latest cached frame).

## Camera exposure captured on Save, replayed on Run

A profile records the camera condition it was tuned under and reproduces it at detection time:

- **Save** (`VisionProfileView.OnSave`) snapshots the camera's live exposure (auto flag + µs)
  into the profile before writing the `.hvp`, so the file reflects the real condition — not a
  stale text box.
- **Run** — before every by-name detection, `ProcessService.RunDetectionAsync` calls
  `ApplyProfileExposureAsync`: it pushes the profile's exposure to the camera (skips if already
  matching), waits a configurable **settle delay** (`AppSettings.VisionExposureSettleMs`, default
  200 ms), then waits for ≥2 fresh `CameraFrame`s (via `FrameReady`) so detection runs on a frame
  captured under the settled exposure — never a mid-transition one. Best-effort: a failed apply is
  logged, detection still runs on the cached frame. The explicit-frame (UI page) path is untouched.

## Consumers of a by-name run

`RunVisionProfileByNameAsync` is the single entry every automated caller shares. On success it
also raises **`IProcessService.VisionProfileRan`** (`VisionProfileDetection(CameraId, ProfileName,
VisionDetectResult)`) so a live front-end can react without having invoked the detection itself.
The same `VisionDetectResult` fans out to:

- **`.moon` scripts** — `machine.vision.runProfile(name)` returns a Lua table `{found, x, y, angle,
  length, …}` (one field per `VisionResultValue`). See `docs/…` / the `.moon` scripting layer.
- **Auto vision U-correction** — `MeasureVisionUCorrectionAsync` runs the per-position facet
  profiles (`P1 Angle`/`P2 Angle`/`F1_Angle`/`F2_Angle`), reads each `angle`, and rotates hex U by
  `PICavg − FAUavg`. Driven by "Start correction" on the Run page; toggled + thresholded on the
  Engineer **Find First Light** tab (`AutoVisionUCorrection`, confirm/fallback degree limits). See
  `docs/faam-ui.md`.
- **Run-page detection flash** — `RunView` subscribes to `VisionProfileRan` and, for a **Line**
  result on the main-feed camera, draws a **yellow line overlay** over the live feed for ~1.5 s
  (`DetectionOverlay` canvas + `DetectedLine`). Source→display mapping mirrors MoveClick (uniform
  min-scale + centre), so the flashed line aligns with the crosshair coordinate system. Other
  methods (NCC/circle) raise the event but are ignored (no line endpoints).
