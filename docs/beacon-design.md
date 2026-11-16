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

## Drawing (`ToolOverlayView.DrawBeacons`)

Beacons are drawn by the shared tool overlay, so they appear on every surface that has it: the
Run-page feed and Zoom inset, the camera pop-out, and the Part Alignment / Gripped FAU pages.

- Position on screen = (beacon − camera device's live position) → pixels with `MmPerPixel` and the
  MoveClick axis signs, the same maths as the part-alignment marks.
- A dot plus two rings that expand and fade once per beat (850 ms, about 70 beats a minute),
  redrawn on the overlay's 10 Hz tick.
- Outside the picture: pinned to the nearest edge, labelled with the distance in mm.
- Drawn whatever the "show tool overlay" setting says — a beacon has its own `show` flag.

Limit: a beacon is a point in the gantry frame, so it is only meaningful on a camera that rides on
the camera device. The overlay does not know which camera that is and draws beacons on any camera
surface.
