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
