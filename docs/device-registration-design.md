# Device registration — tool (gripper) ↔ camera

FAAM's rig has two independent motion systems that need to meet: the **gantry
(`gantry-main`)** carries the camera, the **PI hexapod (`hex-right`)** carries the
gripper. A *device registration* records where the gripper sits in the gantry's
coordinate frame so the process can send the camera to the gripper, the gripper to
the camera, and draw the gripper live on every camera view. It is machine-global
(it describes the physical mount) and persists until the hardware is changed.

## Layers

| Layer | Piece | Role |
|---|---|---|
| 2 `Horizon.Workflow/Registration/` | `ToolRegistration` (+ `ToolAxisMap`, `RegisteredPoint`, `ToolLocation`) | the persisted model |
| | `ToolRegistrationStore` | `%APPDATA%\Horizon\config\tool_registrations.json` — a list keyed by tool device |
| | `ToolRegistrationRoutine` | the maths (`Locate`) and the two bounded moves |
| | `IProcessService` | `GetToolRegistration(s)` / `SaveToolRegistration` / `RemoveToolRegistration` / `LocateTool` / `MoveCameraToToolAsync` / `MoveToolToCameraAsync` + `ToolRegistrationsChanged` |
| | `StepDispatcher` | `tool.cameraToTool`, `tool.toolToCamera` |
| 3 `FAAM.UI` | Machine › **Registration** section (`DeviceRegistrationSectionViewModel` / `…View`) | the registration procedure (Engineer level, `AccessPolicy.Registration`) |
| | `ToolOverlayView` + `ToolOverlayMath` | the live overlay on the Run feed, the Zoom2× inset and the camera pop-out |
| | `OpCatalog` / `OpCatalogHelp` | the two Engineer blocks under **Tool** |

## Geometry

Everything is expressed in the **camera device's frame** (gantry XY, mm).

- A click on the feed at source pixel `p` while the gantry stands at `G` is the point
  `G.xy + (p − centre) · mm/pixel · MoveClick signs` — exactly what a MoveClick jog would
  drive to, so the registration and the jog agree by construction (`ToolOverlayMath`).
- The registration stores the **tool pose** `T0` (full hex-right pose at registration)
  and the **tool centre** `L0 = mean(points)` in the gantry frame.
- A later hexapod pose `T` moves the tool by `MapX.Sign × (T − T0)[MapX.ToolAxis]` along
  gantry X, likewise Y. The two `ToolAxisMap`s are seeded from `jog_transforms.json`
  (`R = M_camera · M_toolᵀ`, dominant column of rows X and Y); for hex-right that is
  **camera X ← tool Z × −1, camera Y ← tool Y × −1**. Editable on the section.
- Live location: `L = L0 + mapped Δtool`; the overlay draws `L − G` converted back to
  pixels. Rotations (U/V/W) are ignored — it is a pure translation model.
- **Focus Z** (optional): the gantry Z at which the gripper was in focus while standing at
  `T0` (`CameraZ`). A third map `MapZ` (hex-right default: camera Z ← tool X × −1) projects
  hexapod moves along the optical axis, so the focus height follows the gripper.

## Procedure (Machine › Registration)

1. **Tool pose** — park the gripper anywhere, *Capture tool pose*. *Move to pose* sends
   it back there later (also the `tool.toPose` block).
2. **Camera on the gripper** — jog the gantry (rail or MoveClick on the feed) until the
   gripper is in view and in focus, then *Capture focus Z*.
3. **Points** — arm *Pick points*, click 2 (opposite edges) or 4 (corners) points on the
   gripper. Each click is converted to the gantry frame immediately, so the camera may be
   re-jogged between clicks; the markers stay glued to the physical spot. The average is
   the tool centre.
4. **Outline** — arm *Pick outline* and click two opposite corners of the gripper body:
   width (image X), length (image Y) and the box's **offset from the tool anchor**
   (`OutlineOffsetX/Ymm`, box centre − anchor) are derived and editable. The anchor is the
   step-3 average and may sit anywhere in the box; the grey box on the feed previews it.
5. **Save** — persists everything. Picking points or corners is refused unless the hexapod
   stands at its captured pose (they describe the tool *at that pose*; use *Move to pose*);
   editing the outline, offsets, label or bound and saving needs no such check. *Delete* /
   *Reload* and two bounded **test** moves sit alongside.

## The two moves

- **Camera → tool** (`MoveCameraToToolAsync`, block `tool.cameraToTool`): one coordinated
  gantry XY move onto `L`; with `includeZ` the gantry Z also goes to the focus height.
- **Tool → camera** (`MoveToolToCameraAsync`, block `tool.toolToCamera`): the mapped
  hexapod axes move by the inverted delta so the tool lands under the camera centre; with
  `includeZ` the Z-mapped axis brings it to the camera's current focus plane **plus a Z
  offset** (`ToolToCameraZOffsetMm` on the registration, overridable per block via
  `zOffset`; negative = higher on the gantry's Z convention, e.g. −1 keeps the gripper
  1 mm above focus). Camera → tool never applies the offset: it always goes to the taught
  focus height.
- **Tool → pose** (`MoveToolToPoseAsync`, block `tool.toPose`): the hexapod returns to
  `T0` (all axes; not bounded — it is a taught location).

Both read the devices live, and both are **bounded**: a required travel longer than the
registration's `MaxMoveMm` (10 mm default; a block may tighten it with `maxDistance`) is
**refused with an error, never clamped** — a clamped move would park the device
somewhere nobody asked for. With `includeZ` the bound covers XYZ; without it Z is untouched.
`includeZ` fails cleanly when no focus Z was captured.

## Tracking arm (test only)

The Registration section has a **Track** checkbox: each sampler tick compares both devices'
polled poses with the last settled snapshot; when one has been jogged (and both are at
rest) the other follows through the same bounded Layer 2 moves as the test buttons —
gantry jogged → tool → camera (with the Z offset when Z is included); hexapod jogged →
camera → tool. After a follow move the snapshot is re-taken after a short settle so the
follow itself never re-triggers. It is switched off when the section is left and is not
available on the Run page or in programs.

## Overlay

`ToolOverlayView` sits over an `Image` the same way the detection-line canvas does and
redraws at ~10 Hz from the engine's cached motion states (no hardware I/O). For every
registration whose `CameraId` matches the shown camera it draws an orange crosshair marker
at the anchor, the grey outline rectangle at anchor + offset (clipped, so a half-visible
gripper still shows its edge) and the label. With a focus Z the label carries the height
read-out — `tool higher −1.00` / `tool lower +1.00` (ToolZ − CameraZ; gantry Z− is up).
When the centre is outside the frame the marker is pinned to the nearest edge with an
arrow toward the real position and the distance in mm (`Gripper 12.30 mm ↗`).

## Gripped object (the FAU in the gripper)

Once the gripper is registered, whatever it holds can be taught the same way and ride
along with it. A **gripped object** is per-product (`{product}\gripped_objects.json`,
copied by Save As…) because the FAU dimensions belong to the product, not the machine.

| Layer | Piece | Role |
|---|---|---|
| 2 `Registration/` | `GrippedObject` (+ `GrippedPoint`), `GrippedObjectStore` | model + per-product store |
| | `ToolRegistrationRoutine.LocateObject` | live location: taught centre carried by the tool's move since the object's tool pose, through the tool registration's axis maps |
| | `IProcessService` | `Get/SaveGrippedObject(s)`, `RemoveGrippedObject`, `LocateGrippedObject`, `GrippedObjectsChanged` |
| 3 `FAAM.UI` | Home › **Vision Alignment** › **Gripped FAU** tab (`VisionAlignmentView` hosts it next to Vision Profile; `GrippedObjectView` / `GrippedObjectViewModel`, tab gated at `AccessPolicy.GrippedObject` = Engineer) | the teach procedure |
| | `ToolOverlayView` | draws every saved object in **purple** on the Run feed, the Zoom2× inset and the pop-out, with `FAU higher/lower` Z read-out |

Procedure (page): **1** capture the tool pose (Move to pose returns there) → **2** camera
exposure (read / apply; lighting is a free-text placeholder for now) → **3** for each of the
two reference points: *Teach camera pos* (records the gantry pose; *Move camera* returns
there and re-applies the exposure), then mark the point either by arming *Mark 1 / Mark 2*
and clicking the feed, or by choosing a vision profile and *Run* (takes the profile's `x`/`y`
result — point-type profiles: NCC, circle, corner) → **4** the centre is the mean → **5**
outline: *Pick outline* two corners (width / length / box offset derived, editable) →
**6** *Save object* into the product; *Clear object* removes the object with its points and
outline. Marking / corner picking is refused unless the tool stands at the object's pose.
The focus Z is the mean camera Z of the two point poses.

Point camera positions are stored both absolute and as a **`CameraOffset` from the tool
centre** (camera pose − `LocateTool`, Z included when the registration has a focus Z), so
*Move camera* / *Run* go to "tool centre + offset" wherever the tool is. *Camera → tool
centre* (step 1) is the reference move. **Step 7 — Run object detect procedure** re-teaches
the object from wherever the tool stands: capture the tool pose, camera → tool centre, then
per point: camera by its offset → detect with the profile (anchor preferred) or wait for the
operator's Mark click → save, keeping the outline size + offset from the centre. Each move
is followed by the object's **settle** wait (`SettleMs`, 100 ms default). The object marker
is purple, its outline green.

**Step 8 — Object → camera centre** (`MoveGrippedObjectToCameraAsync`, block
`object.toCamera`): the gripper moves so the object's centre sits under the camera centre;
with Z its focus plane lands at the camera Z + a **Z offset** (`ToCameraZOffsetMm`, camera
frame: negative = higher = hex-right X+, positive = lower = hex-right X−). Bounded like the
tool moves.

## Part alignment (fiducials + local objects)

Locates the physical part on the working fixture so a program knows where it sits after the
operator loads it. Per-product (`{product}\part_alignments.json`, copied by Save As…).

| Layer | Piece | Role |
|---|---|---|
| 2 `Registration/` | `PartAlignment` (+ `PartFiducial`, `PartObject`, `PartObjectPoint`, `PartFrame`, `PartMeasurement`, `PartSnapshot`), `PartAlignmentStore` | model + per-product store |
| | `PartAlignmentRoutine` | frame maths, detection (anchor XY preferred), the run, camera → fiducial / object |
| | `IProcessService` | `Get/Save/RemovePartAlignment`, `GetPartSnapshot`, `DetectPartPointAsync`, `ToPartPoint`, `RunPartAlignmentAsync(name, PartManualMark?)`, `MoveCameraToPartFiducialAsync`, `MoveCameraToPartObjectAsync`, `PartAlignmentsChanged` |
| | `StepDispatcher` | `part.runAlignment`, `part.cameraToFiducial`, `part.cameraToObject` |
| 3 `FAAM.UI` | Vision Alignment › **Part Alignment** tab (`PartAlignmentView` / `PartAlignmentViewModel`, `AccessPolicy.PartAlignment` = Engineer) | the teach + run UI |
| | `ToolOverlayView` | draws saved parts in **cyan** (E1 / E2 diamonds, object points / lines, an offset cross); dimmer when taught-only |

**Frame.** Origin = E1, +X along E1 → E2, +Y = +X rotated 90° counter-clockwise in the gantry
XY plane (right-handed), units mm, no scaling (`PartFrame`). Object points are stored in this
local frame together with the local camera position they are seen from (camera Z absolute), so
they follow the part wherever the fixture puts it.

**Procedure (tab).** Camera exposure → **1** E1: teach the camera position, mark by click or
profile → **2** E2 likewise → **3** the frame is derived → **4** local objects: `+ Point` /
`+ Line`; selecting one swaps the panel to its editor (per point: camera position, Mark or
profile; an XY offset in the local frame) → **5** Save → **6** Run: camera → E1, detect, →
E2, detect, rebuild the frame, then each object from its taught local camera position
through the new frame (profile, or wait for the operator's Mark click). An object that is
not found keeps its taught position through the new frame and is flagged "(taught)"; the
settle wait applies after each move → **7** camera → E1 / E2 / object (+ offset; a line's
reference is its midpoint). Measured values are used once a run happened, taught ones
before. The moves go to taught positions and are not distance-bounded.

From a program every point must carry a vision profile: manual clicks need the tab, so
`part.runAlignment` fails cleanly on a manual point.

## Marker placement (all teach surfaces)

Every point the operator places by clicking — registration centre points and outline
corners, gripped-object points and corners, part fiducials and object points, and the manual
points a run procedure asks for — goes through `Views/MarkerPlacer`: while armed, a feed click
drops a **yellow crosshair** at that spot with **Confirm / Cancel** floated beside it; the
operator may re-click to move it, and nothing is committed until Confirm. Cancel discards the
placement and leaves the mark armed. The owner view routes its click to `Place`, and commits
the image point on `Confirmed` exactly as a direct click used to.
