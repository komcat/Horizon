# Multi-Device Position Accuracy Trainer — Design

## Overview

The trainer teaches bonding positions using a **primary device** (gantry-main) as the "eye" that sees everything. When a secondary device (e.g., hexapod holding a lens) is involved at a bonding point, we need to:

1. **Teach** where the secondary device is (XY observation via gantry corners)
2. **Teach** the bonding Z height relative to the secondary device surface
3. **Retrace** by re-observing the secondary device with the gantry, computing drift, and applying corrected movement

Each bonding point gets **at most one** secondary device assigned.

**Critical:** Gantry-main operates in global coordinates. Secondary devices (hexapods) have rotated local coordinate frames. All corrections computed in gantry-global space must be transformed to the secondary device's local axes before commanding movement.

---

## Step Layout (4 Steps)

```
STEPS
  1. Bonding Positions       (teach gantry bonding XY — unchanged)
  2. Reference Points        (teach gantry frame refs — unchanged)
  3. Secondary Devices       (NEW — assign + teach secondary per bonding point)
     3.0  XY Teaching        (position device + gantry observation)
     3.1  Bonding Z Teaching (focus-based Z height measurement)
  4. Retrace Bonding         (was Step 3 — now includes secondary alignment)
```

---

## Axis Transformation System (NEW — prerequisite)

### Problem

Gantry-main is the "global" coordinate frame. Secondary devices have rotated axes.
The trainer computes corrections in **gantry-global** XYZ, but must command the
secondary device in its **local** axes.

### Config: `axis_transforms.json`

Located in `%APPDATA%\Horizon\config\`:

```json
[
  {
    "DeviceId": "hex-left",
    "Matrix": {
      "M11": 0.0, "M12": 0.0, "M13": 1.0,
      "M21": 0.0, "M22": 1.0, "M23": 0.0,
      "M31": 1.0, "M32": 0.0, "M33": 0.0
    }
  },
  {
    "DeviceId": "hex-bottom",
    "Matrix": {
      "M11": 0.0, "M12": -1.0, "M13": 0.0,
      "M21": 1.0, "M22":  0.0, "M23": 0.0,
      "M31": 0.0, "M32":  0.0, "M33": 1.0
    }
  },
  {
    "DeviceId": "hex-right",
    "Matrix": {
      "M11":  0.0, "M12":  0.0, "M13": 1.0,
      "M21":  0.0, "M22": -1.0, "M23": 0.0,
      "M31": -1.0, "M32":  0.0, "M33": 0.0
    }
  },
  {
    "DeviceId": "gantry-main",
    "Matrix": {
      "M11": 1.0, "M12": 0.0, "M13":  0.0,
      "M21": 0.0, "M22": 1.0, "M23":  0.0,
      "M31": 0.0, "M32": 0.0, "M33": -1.0
    }
  },
  {
    "DeviceId": "stage-main",
    "Matrix": {
      "M11": 1.0, "M12": 0.0, "M13": 0.0,
      "M21": 0.0, "M22": 1.0, "M23": 0.0,
      "M31": 0.0, "M32": 0.0, "M33": 1.0
    }
  },
  {
    "DeviceId": "xy-table",
    "Matrix": {
      "M11": 1.0, "M12": 0.0, "M13": 0.0,
      "M21": 0.0, "M22": 1.0, "M23": 0.0,
      "M31": 0.0, "M32": 0.0, "M33": 1.0
    }
  }
]
```

Each 3x3 matrix maps **global (X,Y,Z)** to **device-local (X,Y,Z)**:

```
[local_X]   [M11 M12 M13] [global_X]
[local_Y] = [M21 M22 M23] [global_Y]
[local_Z]   [M31 M32 M33] [global_Z]
```

### Examples

**hex-left** matrix:
```
local_X =  global_Z      (M13=1)
local_Y =  global_Y      (M22=1)
local_Z =  global_X      (M31=1)
```
So a gantry delta of (dX=+0.1, dY=+0.2, dZ=+0.3) becomes hex-left movement (+0.3, +0.2, +0.1).

**gantry-main** matrix:
```
local_X =  global_X      (M11=1)
local_Y =  global_Y      (M22=1)
local_Z = -global_Z      (M33=-1)
```
Gantry Z is inverted relative to global Z.

### Implementation: `AxisTransformService`

New class in `Horizon.Core`:

```csharp
public class AxisTransformService
{
    // Loaded from axis_transforms.json
    private Dictionary<string, double[,]> _matrices;

    public (double X, double Y, double Z) GlobalToLocal(
        string deviceId, double gX, double gY, double gZ)
    {
        if (!_matrices.TryGetValue(deviceId, out var m))
            return (gX, gY, gZ); // identity fallback

        return (
            m[0,0]*gX + m[0,1]*gY + m[0,2]*gZ,
            m[1,0]*gX + m[1,1]*gY + m[1,2]*gZ,
            m[2,0]*gX + m[2,1]*gY + m[2,2]*gZ
        );
    }

    public (double X, double Y, double Z) LocalToGlobal(
        string deviceId, double lX, double lY, double lZ)
    {
        // Inverse of the matrix (for orthogonal matrices, inverse = transpose)
        if (!_matrices.TryGetValue(deviceId, out var m))
            return (lX, lY, lZ);

        return (
            m[0,0]*lX + m[1,0]*lY + m[2,0]*lZ,
            m[0,1]*lX + m[1,1]*lY + m[2,1]*lZ,
            m[0,2]*lX + m[1,2]*lY + m[2,2]*lZ
        );
    }
}
```

Note: All transform matrices in this system are orthogonal (rotation + axis swap + sign flip),
so inverse = transpose. No need for full matrix inversion.

### Where Used

| Location | Usage |
|----------|-------|
| JogPanel (existing TODO) | Convert global jog axis to device-local axis |
| Trainer Step 3.0 retrace | Convert gantry-global XY delta to secondary device local axes |
| Trainer Step 3.1 retrace | Convert gantry-global Z delta to secondary device local axis |

---

## STEP 3: Secondary Device Teaching

### 3a. Overview / Assignment Table

Shows all bonding points from Step 1. User assigns a secondary device to each one (or leaves as "None").

```
+------------------------------------------------------------------+
| Secondary Device Assignment                                       |
|                                                                    |
| Assign a secondary device to each bonding point that needs one.   |
| Only one device per bonding point.                                 |
|                                                                    |
| +------+--------+-----------------+--------+--------+----------+  |
| | Teach| Name   | Secondary Device| Method | Status | Actions  |  |
| +------+--------+-----------------+--------+--------+----------+  |
| |[Teach]| Bond1 | [hexapod-1   v] | 2-pt   | --     | [Z-Teach]|  |
| |[Teach]| Bond2 | [hexapod-1   v] | 2-pt   | --     | [Z-Teach]|  |
| |[Teach]| Bond3 | [  None      v] |  --    | --     |          |  |
| +------+--------+-----------------+--------+--------+----------+  |
|                                                                    |
| Device dropdown: None, hexapod-1, hexapod-2, ...                  |
| (lists all connected devices except the primary gantry-main)      |
|                                                                    |
| Method dropdown per row: 2-point, 3-point                         |
| Status: --, XY Taught, XY+Z Taught                                |
+------------------------------------------------------------------+
```

- "Teach" button → Step 3.0 wizard (XY teaching)
- "Z-Teach" button → Step 3.1 wizard (bonding Z) — only enabled after XY taught
- "Status" shows: `--`, `XY Taught`, `XY+Z Taught`

---

### Step 3.0: XY Teaching Wizard (per bonding point)

#### Phase 1: Position the Secondary Device

```
+------------------------------------------------------------------+
| Teach Secondary for "Bond1"                                       |
|                                                                    |
| Step 3.0 Phase 1: Position hexapod-1                              |
|                                                                    |
| Move hexapod-1 to its working position for Bond1,                 |
| then click Confirm.                                                |
|                                                                    |
| hexapod-1 position:                                                |
|   X=-12.3400  Y=5.6700  Z=0.1200  U=0.00  V=0.00  W=0.00        |
|                                                                    |
|  [Confirm Position]   [Cancel]                                     |
+------------------------------------------------------------------+
```

- User jogs hexapod-1 to desired position via Jog panel
- "Confirm Position" records hexapod-1's full 6-axis position
- Advances to Phase 2

#### Phase 2: Observe with Gantry (2-pt or 3-pt)

Same corner-based capture as Steps 1/2, but gantry is looking at the secondary device feature.

```
+------------------------------------------------------------------+
| Teach Secondary for "Bond1"                                       |
|                                                                    |
| Step 3.0 Phase 2: Observe hexapod-1 with gantry (Point 1 of 2)  |
|                                                                    |
| Move gantry-main to see the feature/marker on hexapod-1,          |
| jog to perimeter Point 1, then click Capture.                     |
|                                                                    |
| Camera: [Camera-1          v]                                      |
| gantry-main:  X=-183.3307  Y=-85.4640  Z=-17.8440                |
|                                                                    |
|  [Capture]   [Cancel]                                              |
|                                                                    |
| Captured so far:                                                   |
|  (none yet)                                                        |
+------------------------------------------------------------------+
```

After all corners:
- Auto-move gantry to computed center, capture reference image
- Review screen → Complete
- Records gantry observation center + corners + image
- Status becomes "XY Taught"

---

### Step 3.1: Bonding Z Teaching Wizard (per bonding point)

**Prerequisite:** Step 3.0 must be complete for this bonding point (need the starting positions).

#### Overview

The goal is to measure the **relative Z height** from the top of the secondary device
to the bonding surface, as seen by the gantry's Z axis. This Zrel value is stored and
applied during retrace to move the secondary device to the correct bonding Z.

```
Zrel = Zbond - Zobj - T

Where:
  Zobj  = gantry Z when focused on TOP of secondary device
  T     = user-entered clearance (top surface to bonding plane), default 0.0mm, always >= 0
  Zbond = gantry Z when focused on desired bonding surface
  Zrel  = relative Z offset to apply to secondary device during retrace
```

#### Sub-step 1: Navigate to taught positions

```
+------------------------------------------------------------------+
| Bonding Z Teaching for "Bond1"                                     |
|                                                                    |
| Navigating to taught positions...                                  |
|                                                                    |
| hexapod-1:  X=-12.34  Y=5.67  Z=0.12  (taught from Step 3.0)    |
| gantry-main: X=-183.33  Y=-85.46  Z=-17.84  (observation center) |
|                                                                    |
| Both devices moving to Step 3.0 saved positions.                   |
|                                                                    |
|  [Positions Reached — Continue]   [Cancel]                         |
+------------------------------------------------------------------+
```

- System moves hexapod-1 to taught `devicePosition` from Step 3.0
- System moves gantry to taught `observationCenter` from Step 3.0
- User confirms both are in position

#### Sub-step 2: Focus on top of secondary device → register Zobj

```
+------------------------------------------------------------------+
| Bonding Z Teaching for "Bond1"                                     |
|                                                                    |
| Step 2: Focus on TOP of hexapod-1                                  |
|                                                                    |
| Jog gantry-main Z until the camera is focused on the TOP          |
| surface of the component on hexapod-1.                             |
|                                                                    |
| gantry-main Z: -17.8440                                           |
|                                                                    |
|  [Register Zobj]   [Cancel]                                        |
+------------------------------------------------------------------+
```

- User jogs gantry Z only (XY stays at observation center)
- Click "Register Zobj" saves the current gantry Z as Zobj

#### Sub-step 3: Enter clearance T

```
+------------------------------------------------------------------+
| Bonding Z Teaching for "Bond1"                                     |
|                                                                    |
| Step 3: Enter surface clearance                                    |
|                                                                    |
| Zobj (top surface) = -17.8440 mm                                  |
|                                                                    |
| Distance from top surface to bonding plane (T):                    |
| T = [  0.000  ] mm   (always positive, default 0.0)               |
|                                                                    |
| T accounts for component thickness, standoff, etc.                 |
|                                                                    |
|  [Continue]   [Cancel]                                              |
+------------------------------------------------------------------+
```

- User enters T (distance from top of component to bonding surface)
- T >= 0 always (validated)

#### Sub-step 4: Focus on bonding surface → register Zbond

```
+------------------------------------------------------------------+
| Bonding Z Teaching for "Bond1"                                     |
|                                                                    |
| Step 4: Focus on BONDING SURFACE                                   |
|                                                                    |
| Jog gantry-main Z until the camera is focused on the actual       |
| bonding surface (substrate, target, etc.)                          |
|                                                                    |
| Zobj = -17.8440 mm                                                |
| T    =   0.0000 mm                                                |
| gantry-main Z: -18.2100                                           |
|                                                                    |
|  [Register Zbond]   [Cancel]                                       |
+------------------------------------------------------------------+
```

- User jogs gantry Z to focus on the bonding target
- Click "Register Zbond" saves current gantry Z as Zbond

#### Sub-step 5: Review and save

```
+------------------------------------------------------------------+
| Bonding Z Teaching for "Bond1" — Review                            |
|                                                                    |
| Measurements:                                                      |
|   Zobj  (top of device)  = -17.8440 mm                            |
|   T     (clearance)      =   0.0000 mm                            |
|   Zbond (bonding surface) = -18.2100 mm                            |
|                                                                    |
| Computed:                                                          |
|   Zrel = Zbond - Zobj - T                                         |
|   Zrel = -18.2100 - (-17.8440) - 0.0000                           |
|   Zrel = -0.3660 mm                                               |
|                                                                    |
| During retrace, hexapod-1 Z will be adjusted by Zrel              |
| (transformed to device-local axis via transformation matrix).      |
|                                                                    |
|  [Complete]   [Discard]                                            |
+------------------------------------------------------------------+
```

- Shows the full calculation for transparency
- "Complete" saves Zrel to the bonding point's secondary teaching data
- Status becomes "XY+Z Taught"

---

## STEP 4: Retrace Bonding (updated)

### 4a. Pre-flight — Secondary Device Toggle

```
+------------------------------------------------------------------+
| Retrace Bonding                                                    |
|                                                                    |
| Re-visit reference landmarks to compute corrected bonding          |
| positions.                                                         |
|                                                                    |
| Secondary Device Alignment:                                        |
| +------+--------+-----------+------+--------+---------+            |
| | [x]  | Bond1  | hexapod-1 | 2-pt | XY+Z   | [x] Z  |           |
| | [x]  | Bond2  | hexapod-1 | 2-pt | XY only | [ ] Z  |          |
| |      | Bond3  | (none)    |      |         |         |           |
| +------+--------+-----------+------+--------+---------+            |
|                                                                    |
| Left checkbox: enable secondary XY alignment                       |
| Right checkbox: also apply Zrel adjustment (only if Z was taught)  |
|                                                                    |
|  [Start Alignment]   [Accept Ref]                                  |
+------------------------------------------------------------------+
```

- Left checkbox: enable/disable secondary XY alignment for this point
- Right "Z" checkbox: also apply Zrel (only shown/enabled if Step 3.1 was completed)

### 4b. Phase 1 — Gantry Reference Alignment (unchanged)

Same as current:
1. Move gantry to taught Ref1 → user adjusts → "Accept Ref"
2. Move gantry to taught Ref2 → user adjusts → "Accept Ref"
3. Compute corrected gantry bonding positions (coordinate frame math)

### 4c. Phase 2 — Secondary Device XY Alignment (for each enabled point)

#### Sub-step A: Navigate to taught positions

```
+------------------------------------------------------------------+
| Secondary Alignment: Bond1 (1 of 2)                               |
|                                                                    |
| Moving hexapod-1 to taught position...                             |
| Moving gantry-main to taught observation position...               |
|                                                                    |
| hexapod-1:  X=-12.34  Y=5.67  Z=0.12                             |
| gantry-main: X=-183.33  Y=-85.46  Z=-17.84                       |
|                                                                    |
| Reference image:  [thumbnail from teaching]                        |
|                                                                    |
|  [Begin Observation]   [Skip This Point]                           |
+------------------------------------------------------------------+
```

#### Sub-step B: Re-observe with Gantry (same method as teaching)

```
+------------------------------------------------------------------+
| Secondary Alignment: Bond1 — Observe (Point 1 of 2)               |
|                                                                    |
| Move gantry-main to perimeter Point 1 of hexapod-1,               |
| then click Capture.                                                |
|                                                                    |
| Camera: [Camera-1          v]                                      |
| gantry-main: X=-183.3307  Y=-85.4640  Z=-17.8440                 |
|                                                                    |
| Taught corner 1 was: X=-183.50  Y=-85.60                          |
|                                                                    |
|  [Capture]   [Cancel]                                              |
+------------------------------------------------------------------+
```

#### Sub-step C: Compute correction and move

```
+------------------------------------------------------------------+
| Secondary Alignment: Bond1 — Result                                |
|                                                                    |
| Gantry observation (global frame):                                 |
|   Taught:  X=-183.33  Y=-85.46                                    |
|   Actual:  X=-183.40  Y=-85.50                                    |
|   Delta:   dX=-0.07   dY=-0.04                                    |
|                                                                    |
| Transform to hexapod-1 local axes:                                 |
|   hex-left matrix: globalX->localZ, globalY->localY, globalZ->localX
|   Local delta: dX_local=0.00  dY_local=-0.04  dZ_local=-0.07     |
|                                                                    |
| hexapod-1 correction (local axes):                                 |
|   Taught:   X=-12.34   Y=5.67    Z=0.12                           |
|   Adjusted: X=-12.34   Y=5.63    Z=0.05                           |
|                                                                    |
| Zrel adjustment: -0.3660 mm (applied to local Z)  [if Z enabled]  |
|   Final Z: 0.05 + (-0.3660) = -0.3160                             |
|                                                                    |
|  [Accept & Move hexapod-1]   [Skip]                                |
+------------------------------------------------------------------+
```

### Correction Math (updated with transforms)

```
// --- XY Correction ---

// Step 1: Compute gantry-global delta from observation
delta_global = (
    actual_obs.X - taught_obs.X,    // dX_global
    actual_obs.Y - taught_obs.Y,    // dY_global
    0                                // no Z delta from observation
)

// Step 2: Transform global delta to secondary device local axes
delta_local = AxisTransformService.GlobalToLocal(deviceId, delta_global)

// Step 3: Apply to taught device position (which is in local axes)
corrected_local.X = taught_device.X + delta_local.X
corrected_local.Y = taught_device.Y + delta_local.Y
corrected_local.Z = taught_device.Z + delta_local.Z

// --- Z Correction (optional, from Step 3.1) ---

// Zrel is measured in gantry-global Z
// Transform Zrel to device-local using the Z column of the matrix
zrel_global = (0, 0, Zrel)
zrel_local = AxisTransformService.GlobalToLocal(deviceId, zrel_global)

corrected_local.X += zrel_local.X
corrected_local.Y += zrel_local.Y
corrected_local.Z += zrel_local.Z

// U/V/W always unchanged
corrected_local.U = taught_device.U
corrected_local.V = taught_device.V
corrected_local.W = taught_device.W
```

#### Worked Example: hex-left

```
Matrix:  [0 0 1]    global_X -> local_Z
         [0 1 0]    global_Y -> local_Y
         [1 0 0]    global_Z -> local_X

Gantry delta: dX_global=-0.07, dY_global=-0.04, dZ_global=0
After transform: dX_local=0.00, dY_local=-0.04, dZ_local=-0.07

Taught hex-left:  X=-12.34, Y=5.67, Z=0.12
Corrected:        X=-12.34, Y=5.63, Z=0.05

Zrel = -0.3660 (in global Z)
Zrel global vector = (0, 0, -0.3660)
After transform:   dX_local=-0.3660, dY_local=0, dZ_local=0
   (because global_Z maps to local_X for hex-left)

Final:  X = -12.34 + 0.00 + (-0.3660) = -12.706
        Y = 5.63 + 0 = 5.63
        Z = 0.05 + 0 = 0.05
```

### 4d. Phase 3 — Final "Go" Behavior

After all alignments complete, the results grid shows:

```
+----------------------------------------------------------------------+
| Retrace Complete                                                      |
|                                                                        |
| +----+-------+----------+----------+-----+-------+-------+---------+ |
| | Go | Name  | Corr. X  | Corr. Y  |  Z  | dX    | dY    | 2nd Dev | |
| +----+-------+----------+----------+-----+-------+-------+---------+ |
| |[Go]| Bond1 | -183.40  | -85.50   |-17.8| -0.07 | -0.04 | hex-left| |
| |[Go]| Bond2 | -190.12  | -80.23   |-17.8| -0.06 | -0.03 | hex-left| |
| |[Go]| Bond3 | -195.55  | -75.10   |-17.8| -0.08 | -0.05 | (none)  | |
| +----+-------+----------+----------+-----+-------+-------+---------+ |
+----------------------------------------------------------------------+
```

**Clicking "Go" on Bond1:**
1. Move gantry-main to corrected XY (bonding position only — gantry goes to bonding XY)
2. Move hexapod-1 to adjusted local position (XY corrected + optional Zrel)

**Note:** Gantry "Go" moves to the corrected **bonding** XY from Step 1 frame correction.
The gantry does NOT go to the observation position — it goes to the bonding position.
The observation position was only used during retrace Phase 2 to re-observe the secondary device.

---

## Complete Workflow Summary

### Teaching (one-time setup)

```
Step 1: Bonding Positions (gantry only — UNCHANGED)
  For each bonding point:
    a. Enter name, select 2-pt or 3-pt
    b. Jog gantry to corner 1 -> Capture
    c. Jog gantry to corner 2 -> Capture
    d. (optional corner 3)
    e. Auto-move to center, capture image
    f. Review -> Complete
    Result: bonding point with gantry XYZ center

Step 2: Reference Points (gantry only — UNCHANGED)
  For each reference (need >= 2):
    a. Select 2-pt or 3-pt
    b. Jog gantry to corner 1 -> Capture
    c. Jog gantry to corner 2 -> Capture
    d. Auto-move to center, capture image
    e. Review -> Complete
  Then: Compute Frame (coordinate axes from ref1 + ref2)
  Result: coordinate frame for gantry corrections

Step 3.0: Secondary Device XY Teaching (NEW)
  For each bonding point that needs a secondary device:
    a. Select secondary device from dropdown (e.g., hexapod-1)
    b. Select observation method (2-pt or 3-pt)
    c. Click "Teach"

    Phase 1 — Position secondary:
      d. Jog hexapod-1 to working position
      e. Click "Confirm Position"
         -> Records hexapod-1 full 6-axis position (local axes)

    Phase 2 — Observe with gantry:
      f. Jog gantry to see hexapod-1 feature, corner 1 -> Capture
      g. Jog gantry corner 2 -> Capture
      h. (optional corner 3)
      i. Auto-move gantry to center, capture reference image
      j. Review -> Complete
         -> Records gantry observation center + corners + image

    Result: "XY Taught" status

Step 3.1: Secondary Device Bonding Z Teaching (NEW)
  Prerequisite: Step 3.0 complete for this bonding point

    a. Click "Z-Teach" for the bonding point
    b. System navigates both devices to Step 3.0 saved positions
    c. User confirms positions reached
    d. User jogs gantry Z to focus on TOP of device -> Register Zobj
    e. User enters clearance T (distance top->bonding plane, default 0.0mm)
    f. User jogs gantry Z to focus on bonding surface -> Register Zbond
    g. System computes: Zrel = Zbond - Zobj - T
    h. Review -> Complete
       -> Saves Zrel to secondary teaching data

    Result: "XY+Z Taught" status
```

### Retracing (production use, repeatable)

```
Step 4: Retrace Bonding
  Pre-flight:
    a. Toggle which secondary devices to XY-align (checkboxes)
    b. Toggle which to also Z-adjust (separate checkbox, only if Z taught)

  Phase 1 — Gantry reference alignment (UNCHANGED):
    c. Move gantry to Ref1 taught position
    d. User adjusts gantry -> Accept Ref
    e. Move gantry to Ref2 taught position
    f. User adjusts gantry -> Accept Ref
    g. Compute corrected gantry bonding positions
       (using coordinate frame math — unchanged)

  Phase 2 — Secondary device alignment (for each enabled point):
    h. Move hexapod-1 to taught position (local axes)
    i. Move gantry to taught observation center (global axes)
    j. User clicks "Begin Observation"
    k. Jog gantry to corner 1 of hexapod-1 feature -> Capture
    l. Jog gantry to corner 2 -> Capture
    m. Compute new observation center (global axes)
    n. Compute global delta:
         dX_global = new_center.X - taught_center.X
         dY_global = new_center.Y - taught_center.Y
    o. Transform to device-local axes:
         (dX_local, dY_local, dZ_local) = GlobalToLocal(deviceId, dX_global, dY_global, 0)
    p. If Z enabled, also transform Zrel:
         (zX, zY, zZ) = GlobalToLocal(deviceId, 0, 0, Zrel)
    q. Final corrected position (local axes):
         X = taught.X + dX_local + zX
         Y = taught.Y + dY_local + zY
         Z = taught.Z + dZ_local + zZ
         U, V, W = taught (unchanged)
    r. User clicks "Accept & Move" -> hexapod-1 moves

  Final:
    s. Show corrected positions grid
    t. "Go" moves gantry to corrected BONDING XY + secondary to adjusted position
```

---

## Data Model Changes

### New: `DeviceSnapshot` record (in TrainerModels.cs)

```csharp
public record DeviceSnapshot
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = "";

    [JsonPropertyName("x")]  public double X { get; init; }
    [JsonPropertyName("y")]  public double Y { get; init; }
    [JsonPropertyName("z")]  public double Z { get; init; }
    [JsonPropertyName("u")]  public double U { get; init; }
    [JsonPropertyName("v")]  public double V { get; init; }
    [JsonPropertyName("w")]  public double W { get; init; }
}
```

### New: `SecondaryTeaching` record (in TrainerModels.cs)

```csharp
public record SecondaryTeaching
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = "";

    [JsonPropertyName("method")]
    public string Method { get; init; } = "2-point";

    [JsonPropertyName("devicePosition")]
    public DeviceSnapshot DevicePosition { get; init; } = new();

    [JsonPropertyName("observationCorners")]
    public List<BondingCorner> ObservationCorners { get; init; } = new();

    [JsonPropertyName("observationCenterX")]
    public double ObservationCenterX { get; init; }

    [JsonPropertyName("observationCenterY")]
    public double ObservationCenterY { get; init; }

    [JsonPropertyName("observationCenterZ")]
    public double ObservationCenterZ { get; init; }

    [JsonPropertyName("centerImagePath")]
    public string? CenterImagePath { get; init; }

    // Step 3.1 bonding Z data
    [JsonPropertyName("zObj")]
    public double? ZObj { get; init; }

    [JsonPropertyName("clearanceT")]
    public double? ClearanceT { get; init; }

    [JsonPropertyName("zBond")]
    public double? ZBond { get; init; }

    [JsonPropertyName("zRel")]
    public double? ZRel { get; init; }

    [JsonIgnore]
    public bool HasXYTeaching => !string.IsNullOrEmpty(DeviceName);

    [JsonIgnore]
    public bool HasZTeaching => ZRel.HasValue;
}
```

### Updated: `BondingPoint` (in TrainerModels.cs)

```csharp
public record BondingPoint
{
    // ... existing fields unchanged ...

    [JsonPropertyName("secondaryTeaching")]
    public SecondaryTeaching? SecondaryTeaching { get; init; }
}
```

### New: `AxisTransformConfig` (in Horizon.Core or Horizon.Common)

```csharp
public record TransformEntry
{
    [JsonPropertyName("DeviceId")]
    public string DeviceId { get; init; } = "";

    [JsonPropertyName("Matrix")]
    public TransformMatrix Matrix { get; init; } = new();
}

public record TransformMatrix
{
    public double M11 { get; init; }
    public double M12 { get; init; }
    public double M13 { get; init; }
    public double M21 { get; init; }
    public double M22 { get; init; }
    public double M23 { get; init; }
    public double M31 { get; init; }
    public double M32 { get; init; }
    public double M33 { get; init; }
}
```

### Runtime: `SecondaryCorrection` (in TrainerPanel.xaml.cs)

```csharp
public class SecondaryCorrection
{
    public string DeviceName { get; set; } = "";
    public double DeltaXGlobal { get; set; }
    public double DeltaYGlobal { get; set; }
    public double DeltaXLocal { get; set; }
    public double DeltaYLocal { get; set; }
    public double DeltaZLocal { get; set; }
    public double FinalX { get; set; }
    public double FinalY { get; set; }
    public double FinalZ { get; set; }
    public double U { get; set; }
    public double V { get; set; }
    public double W { get; set; }
    public bool ZEnabled { get; set; }
    public double? ZRel { get; set; }
}
```

---

## Files to Create/Modify

| File | Change |
|---|---|
| `src/Horizon.Core/AxisTransformService.cs` | **NEW** — load config, GlobalToLocal/LocalToGlobal |
| `src/Horizon.Core/TrainerModels.cs` | Add DeviceSnapshot, SecondaryTeaching, update BondingPoint |
| `src/Horizon.Core/AppContext.cs` | Add AxisTransformService property, load during init |
| `src/Horizon.Common/ConfigLoader.cs` | Already supports loading JSON arrays (verify) |
| `src/Horizon.App/TrainerPanel.xaml` | Add Step 3 UI (assignment table + 3.0/3.1 wizards), update Step 4 |
| `src/Horizon.App/TrainerPanel.xaml.cs` | Add Step 3/3.1 logic, update Step 4 retrace with transforms |
| `src/Horizon.App/JogPanel.xaml.cs` | Replace TODO with AxisTransformService usage |

---

## Backward Compatibility

- `SecondaryTeaching` is nullable on `BondingPoint` — old JSON without it deserializes as null
- `ZObj`, `ClearanceT`, `ZBond`, `ZRel` are nullable doubles — absent = no Z teaching
- `axis_transforms.json` missing = identity transform for all devices (no-op)
- Old datasets: Step 3 shows empty assignment table, Step 4 skips Phase 2 entirely
- Previous `DeviceSnapshots` list on BondingPoint/ReferencePoint should be removed
