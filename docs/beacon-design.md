# Beacons (`beacon.plant` / `beacon.remove`)

A beacon is a virtual marker a program plants where the camera centre is. It remembers that spot
in the camera device's axis values (gantry-main X / Y / Z), and the live views draw a pulsing
circle there wherever the camera moves afterwards.

## Blocks

| Block | Params | What it does |
|---|---|---|
| `beacon.plant` — Plant Beacon Here | `name` (blank = next free `B1`, `B2`…), `show` (default on) | Reads the camera device's position and plants a beacon there. |
| `beacon.remove` — Remove Beacon | `which` = `all` \| `name`, `name` | Removes every beacon, or the named one. A name that does not exist is logged, not an error. |

Both are device-less (Control lane); the camera device is `AppSettings.MoveClickDevice`.

## Rules (`Beacons/BeaconRegistry.cs`)

- **In memory only.** Not saved, not per product; gone when the application closes.
- **Lifetime 30 minutes**, or until removed. A beacon is **not** removed when its program ends.
- **At most 10.** Planting an 11th replaces the oldest (logged as a warning).
- **Colours** cycle through a fixed set of 10 in planting order.
- Planting an **existing name** moves that beacon: it keeps its colour and its 30 minutes start again.
- `show` off: the beacon exists and counts toward the 10, but is not drawn.

`IProcessService` exposes the same operations for a head to use directly: `GetBeacons`,
`PlantBeaconAsync`, `RemoveBeacon`, `ClearBeacons`, `BeaconsChanged`.

## Gripped-object marker (`object.register` — Register Gripped Object)

A beacon can **ride on a tool** instead of staying on the machine. `object.register` makes one from
a Detect Object result, so the object held by the gripper keeps a marker that follows the gripper.

| Param | Default | Meaning |
|---|---|---|
| `name` | `FAU` | Marker name (also what Remove Beacon takes). |
| `tool` | the head's tool device (hex-right) | The registered tool device holding the object. |
| `from` | `obj` | Capture prefix of the Detect Object block: reads `{from}_found`, `{from}_x`, `{from}_y`. |
| `show` | on | Draw it on the live view. |

Typical sequence: camera over the gripper → **Detect Object** (prefix `obj`) → **Register Gripped
Object**. The gripper must not move between the two: the block reads the tool's pose when it runs
and ties the detected point to that pose.

- The beacon stores the detected X / Y (camera device frame) plus the tool's pose at registration
  (`Beacon.ToolDevice`, `Beacon.ToolPose`).
- Its live place = that point + the tool's travel since, turned into camera X / Y by the tool's
  registration axis map (`ToolRegistrationRoutine.CarriedPoint` — the same model as a taught
  gripped object's centre). `IProcessService.LocateBeacon` returns it from cached motion state.
- The tool must be registered (Machine › Registration); otherwise the step fails. A result with
  `found = 0` also fails the step — there is nothing to register.
- It is a beacon in every other way: shares the limit of 10 and the colours, lives 30 minutes,
  in memory only, removed by Remove Beacon. Registering the same name again replaces it.
- Rotation of the tool (U / V / W) does not move the marker; only the mapped linear axes do.

### Outline snapshot (optional)

With `outline` ticked the block takes a snapshot **before** it plants the marker, so the object's
shape — not only a dot — follows the gripper on the live view.

| Param | Default | Meaning |
|---|---|---|
| `outline` | off | Take the snapshot. |
| `roiWidth`, `roiHeight` | `300` | The box, in image pixels, centred on the crosshair (16–2000, clamped to the frame). |
| `filters` | (hidden) | The filter chain as JSON, set by **Outline helper…** — the Vision helper's stages (binary threshold, gaussian, canny), up to three. |

- Run time (`GrippedOutlineCapture`): a fresh frame from the tool registration's camera → the
  centred box through `IVisionFacet.ApplyFilterAsync` → pixels ≥ 128 are white. The mask
  (`BeaconOutline`, one byte per pixel) is kept on the beacon, anchored at the camera device's
  position (the crosshair) at that moment.
- The live views paint the white pixels translucent yellow at that place, carried by the tool
  exactly like the marker; black stays transparent. Image scale is 1 mask pixel = 1 source pixel.
- Best-effort: if the snapshot cannot be taken (camera off, HALCON missing) the marker is still
  registered and a warning is logged.
- With no filter the box is plain grayscale thresholded at 128 — pick a binary threshold or canny.
- **Outline helper…** (`GrippedOutlineHelperWindow`, from the block's panel): live view with the
  dashed box and the yellow overlay refreshed a few times a second, width / height fields, the
  three filter rows, the black / white result, and **Save to block** (which also ticks `outline`).

It is a marker only. It does not change the product's taught gripped object (Vision Alignment ›
Gripped FAU), and `object.toCamera` still uses that taught object, not this marker.

## Drawing (`ToolOverlayView.DrawBeacons`)

Beacons are drawn by the shared tool overlay, so they appear on every surface that has it: the
Run-page feed and Zoom inset, the camera pop-out, and the Part Alignment / Gripped FAU pages.

- Position on screen = (beacon − camera device's live position) → pixels with `MmPerPixel` and the
  MoveClick axis signs, the same maths as the part-alignment marks.
- A dot plus two rings that expand and fade once per beat (850 ms, about 70 beats a minute),
  redrawn on the overlay's 10 Hz tick.
- Outside the picture: pinned to the nearest edge, labelled with the distance in mm.
- A beacon that rides on a tool is labelled `name · tool device` and is not drawn while it cannot
  be placed (tool registration deleted, no motion state yet).
- Drawn whatever the "show tool overlay" setting says — a beacon has its own `show` flag.

Limit: a beacon is a point in the gantry frame, so it is only meaningful on a camera that rides on
the camera device. The overlay does not know which camera that is and draws beacons on any camera
surface.
