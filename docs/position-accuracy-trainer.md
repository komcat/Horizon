# Position Accuracy Trainer

The Position Accuracy Trainer is a 3-step wizard-based tool that teaches bonding positions relative to reference landmarks, then corrects those positions at runtime when the workpiece shifts, rotates, or scales. It enables repeatable placement accuracy across production runs without re-teaching every point.

---

## Table of Contents

- [Concepts](#concepts)
- [Architecture](#architecture)
- [Data Model](#data-model)
- [3-Step Workflow](#3-step-workflow)
  - [Step 1: Teach Bonding Positions](#step-1-teach-bonding-positions)
  - [Step 2: Teach Reference Points](#step-2-teach-reference-points)
  - [Step 3: Retrace (Alignment Run)](#step-3-retrace-alignment-run)
- [Coordinate Math](#coordinate-math)
  - [Coordinate Frame Construction](#coordinate-frame-construction)
  - [World-to-Local Transform](#world-to-local-transform)
  - [Local-to-World Transform](#local-to-world-transform)
  - [Scale Compensation](#scale-compensation)
  - [Corrected Position Computation](#corrected-position-computation)
- [Corner-Based Teaching](#corner-based-teaching)
- [Camera Integration](#camera-integration)
- [Persistence](#persistence)
- [UI Layout](#ui-layout)
- [File Locations](#file-locations)
- [Source Files](#source-files)

---

## Concepts

| Term | Definition |
|---|---|
| **Bonding Point** | A target position where work is performed (e.g., wire bond, dispense, place). Defined by its perimeter corners; the center is computed automatically. |
| **Reference Point** | A known landmark on the workpiece (e.g., fiducial mark, alignment feature). Used to establish the coordinate frame. At least 2 are required. |
| **Training Set** | A named collection of bonding points + reference points + the computed coordinate frame. Persisted as a JSON file. |
| **Corner** | A perimeter point captured during teaching. 2 or 3 corners define each bonding or reference point. Their centroid becomes the point's global position. |
| **Coordinate Frame** | A local 2D coordinate system derived from Reference 1 (origin) and Reference 2 (X-axis direction). All bonding positions are stored in this frame. |
| **Retrace / Alignment** | The runtime step where the operator re-visits the reference points. The system computes a new frame from the actual positions and transforms all bonding points into corrected world coordinates. |

---

## Architecture

```
Horizon.Core                          Horizon.App
+------------------+                  +------------------+
| TrainerModels.cs |  <-- used by --> | TrainerPanel.xaml |
|   BondingCorner   |                 | TrainerPanel.cs   |
|   BondingPoint    |                 | DatasetDialog.xaml |
|   ReferenceCorner |                 | DatasetDialog.cs   |
|   ReferencePoint  |                 +------------------+
|   TrainingSet     |
+------------------+
| TrainerMath.cs   |  <-- pure math, no dependencies
|   ComputeCoordinateAxes()
|   WorldToLocal()
|   LocalToWorld()
|   ComputeScale()
|   ComputeCorrectedPosition()
|   ComputeCenter()
|   ComputeRefCenter()
+------------------+
| TrainerStore.cs  |  <-- JSON persistence to %APPDATA%
|   Load / Save / Delete / GetTrainingSetNames()
+------------------+
```

All math is in `TrainerMath` (static, pure functions, no side effects). All persistence is in `TrainerStore` (JSON files). The UI panel (`TrainerPanel`) orchestrates the wizard flow, hardware interaction (motion + camera), and calls into both.

---

## Data Model

### TrainingSet

The top-level container. One JSON file per training set.

```
TrainingSet
  +-- Uid: string (GUID)
  +-- Name: string (also the filename stem)
  +-- DeviceName: string (motion device, e.g., "gantry-main")
  +-- CreatedAt: string (ISO 8601)
  +-- OriginX, OriginY: double (frame origin = Ref1 position)
  +-- XAxisX, XAxisY: double (unit vector along Ref1 -> Ref2)
  +-- YAxisX, YAxisY: double (perpendicular unit vector)
  +-- ReferencePoints: List<ReferencePoint>
  +-- BondingPoints: List<BondingPoint>
  +-- Valid: bool (true after frame is computed)
```

### BondingPoint

```
BondingPoint
  +-- Name: string (user-assigned, e.g., "Bond1")
  +-- Corners: List<BondingCorner> (2 or 3 perimeter points)
  +-- GlobalX, GlobalY, GlobalZ: double (centroid in world coords)
  +-- LocalX, LocalY: double (position in the reference frame)
  +-- CenterImagePath: string? (camera snapshot at computed center)
  +-- Valid: bool
```

### ReferencePoint

```
ReferencePoint
  +-- Uid: string (GUID)
  +-- Name: string (auto-assigned: "Ref1", "Ref2", ...)
  +-- Corners: List<ReferenceCorner> (2 or 3 perimeter points)
  +-- GlobalX, GlobalY, GlobalZ: double (centroid in world coords)
  +-- CenterImagePath: string? (camera snapshot at computed center)
  +-- ImagePath: string? (legacy/alternate image)
  +-- Valid: bool
```

### Corner Records

Both `BondingCorner` and `ReferenceCorner` share the same structure:

```
Corner
  +-- Uid: string (GUID)
  +-- X, Y, Z: double (world coordinates at capture time)
  +-- ImagePath: string? (camera snapshot at this corner)
```

### Supporting Types (UI only)

| Type | Purpose |
|---|---|
| `CorrectedPosition` | Holds retrace results: corrected XYZ, delta from taught, scale factor |
| `CameraItem` | ComboBox item wrapping camera Id + DisplayName |
| `CornerDisplayItem` | ListBox display item for captured corners (label, coords, image path) |

---

## 3-Step Workflow

### Step 1: Teach Bonding Positions

**Goal:** Capture the positions where work will be performed.

```
[Start] --> Enter name, select corner count (2 or 3)
   |
   v
[Capture] --> Jog to perimeter point 1, click Capture
   |              (records XYZ, captures camera image)
   v
[Capture] --> Jog to perimeter point 2, click Capture
   |              (if 3-corner mode: repeat for point 3)
   v
[Auto-Move] --> System computes centroid, moves stage to center
   |              Captures reference image at center position
   v
[Review] --> Shows all corners + computed center
   |         User clicks "Complete" to accept or "Discard" to retry
   v
[Saved] --> BondingPoint added to training set
```

**Key behaviors:**
- Default name auto-increments: `Bond1`, `Bond2`, etc.
- Corner count is selected before capture begins (2-point or 3-point)
- After all corners are captured, the stage automatically moves to the computed centroid
- A 1500ms settle delay is applied before capturing the center reference image
- Each bonding point can be individually re-taught or deleted from the data grid

**Step completion indicator:** Green when at least 1 valid bonding point exists.

### Step 2: Teach Reference Points

**Goal:** Establish landmarks that define the coordinate frame.

```
[Start] --> Select corner count (2 or 3), click Teach
   |
   v
[Capture] --> Jog to perimeter point 1 of RefN, click Capture
   |              (same corner-based flow as Step 1)
   v
[Capture] --> Jog to perimeter point 2, click Capture
   |              (if 3-corner: repeat for point 3)
   v
[Auto-Move] --> System computes centroid, moves to center, captures image
   v
[Review] --> Shows corners + center, Complete or Discard
   v
[Repeat] --> Teach next reference point (minimum 2 required)
   v
[Compute Frame] --> Click "Compute Frame" to build the coordinate system
                    Converts all bonding points to local coordinates
```

**Key behaviors:**
- Names auto-assigned: `Ref1`, `Ref2`, `Ref3`, etc.
- Minimum 2 valid reference points required to compute a frame
- "Compute Frame" derives the coordinate axes from Ref1 (origin) and Ref2 (X-axis direction)
- All existing bonding points are converted to local (frame-relative) coordinates and saved
- Frame status displays origin and X-axis unit vector when valid

**Step completion indicator:** Green when >= 2 valid reference points exist.

### Step 3: Retrace (Alignment Run)

**Goal:** Correct bonding positions for the current workpiece placement.

```
[Start Alignment] --> Requires a valid training set (Steps 1-2 complete)
   |
   v
[Move to Ref1] --> Stage moves to taught Ref1 position
   |                Reference thumbnail image shown for visual comparison
   |                Operator jogs to match the actual landmark
   v
[Accept Ref1] --> Records actual Ref1 position
   v
[Move to Ref2] --> Stage moves to taught Ref2, operator adjusts
   v
[Accept Ref2] --> Records actual Ref2 position
   v
[Compute Corrections] --> New frame from actual refs
                          Scale factor = actual distance / taught distance
                          Each bonding point transformed: local -> new world
   v
[Results Table] --> Shows corrected XYZ, delta from taught, scale
                    "Go" button moves to each corrected position
```

**Key behaviors:**
- Iterates through all valid reference points sequentially
- At each reference, the taught center image is shown as a thumbnail for visual alignment
- The operator jogs the stage to fine-tune alignment, then clicks Accept
- After all references are accepted, corrections are computed automatically
- The corrected positions table shows delta values for verification
- Each corrected position has a "Go" button for immediate stage movement

---

## Coordinate Math

All math lives in `TrainerMath` (`Horizon.Core/TrainerMath.cs`) as static pure functions.

### Coordinate Frame Construction

Given two reference points, constructs a 2D orthonormal frame:

```
Input:  Ref1 (origin), Ref2 (X-axis direction)
Output: origin(x,y), xAxis(x,y), yAxis(x,y)

dx = Ref2.X - Ref1.X
dy = Ref2.Y - Ref1.Y
len = sqrt(dx^2 + dy^2)

xAxis = (dx/len, dy/len)       -- unit vector along Ref1->Ref2
yAxis = (-xAxisY, xAxisX)      -- perpendicular (90 deg CCW)
origin = (Ref1.X, Ref1.Y)
```

If the two references are coincident (`len < 1e-12`), falls back to identity axes `(1,0), (0,1)`.

### World-to-Local Transform

Converts a world position to the local reference frame:

```
dx = worldX - originX
dy = worldY - originY
localX = dx * xAxisX + dy * xAxisY    -- projection onto X-axis
localY = dx * yAxisX + dy * yAxisY    -- projection onto Y-axis
```

Used during Step 2 "Compute Frame" to store bonding points in frame-relative coordinates.

### Local-to-World Transform

Converts a local position back to world coordinates:

```
worldX = originX + localX * xAxisX + localY * yAxisX
worldY = originY + localX * xAxisY + localY * yAxisY
```

Used during Step 3 to transform bonding points into the live (shifted) world frame.

### Scale Compensation

Computes the ratio between actual and taught reference distances:

```
taughtDist = distance(taughtRef1, taughtRef2)
actualDist = distance(actualRef1, actualRef2)
scale = actualDist / taughtDist
```

If `taughtDist < 1e-12`, returns `1.0` (no scaling). This handles thermal expansion, substrate variation, or mounting differences.

### Corrected Position Computation

Combines scale and frame transform to produce the final corrected position:

```
scaledLocalX = bond.LocalX * scale
scaledLocalY = bond.LocalY * scale
(correctedX, correctedY) = LocalToWorld(scaledLocalX, scaledLocalY, liveOrigin, liveAxes)
correctedZ = bond.GlobalZ    -- Z is not transformed
```

The Z coordinate is preserved from the original teaching (no Z correction).

### Center Computation

Both `ComputeCenter` (bonding corners) and `ComputeRefCenter` (reference corners) compute the arithmetic mean of corner XY positions. Z is taken from the first corner.

```
centerX = mean(corners[*].X)
centerY = mean(corners[*].Y)
centerZ = corners[0].Z
```

---

## Corner-Based Teaching

Rather than teaching a single point, the trainer captures 2 or 3 perimeter corners and computes their centroid. This approach provides:

1. **Repeatability** - Edges/corners of features are easier to locate visually than centers
2. **Geometric validation** - Multiple points define the feature boundary, allowing future shape checks
3. **Flexibility** - Supports both 2-corner (line midpoint) and 3-corner (triangle centroid) modes
4. **Image evidence** - Each corner and the computed center get camera snapshots for traceability

### 2-Corner Mode

Best for features with two distinct edges (e.g., bond pad left/right edges). The center is the midpoint.

### 3-Corner Mode

Best for triangular or asymmetric features. The center is the triangle centroid (average of all three vertices).

---

## Camera Integration

The trainer optionally captures images at each corner and at the computed center:

1. **Camera selection** - A ComboBox populated from `CameraManager.GetCamerasWithNames()` lets the operator choose which camera to use
2. **Image capture** - Calls `CameraManager.CaptureImage(cameraId, filename)` with the trainer image directory
3. **Directory management** - Temporarily redirects the camera output directory to the trainer's image folder, then restores the previous directory
4. **File naming** - Images are named by corner/center UID: `{guid}.bmp`
5. **Thumbnail display** - During Step 3 retrace, the taught center image is shown as a reference thumbnail alongside current coordinates

If no camera is connected or capture fails, the wizard continues without images (graceful degradation).

---

## Persistence

### TrainerStore

`TrainerStore` manages all file I/O:

| Method | Description |
|---|---|
| `GetTrainingSetNames()` | Lists all `*.json` files in the trainer directory |
| `Load(name)` | Deserializes a training set from `{name}.json` |
| `Save(set)` | Serializes and writes `{set.Name}.json` |
| `Delete(name)` | Deletes `{name}.json` |
| `GetFilePath(name)` | Returns full path for a named set |
| `GetImageDirectory()` | Returns (and creates) the `images/` subdirectory |

### JSON Format

Uses `System.Text.Json` with:
- `WriteIndented = true` (human-readable)
- `PropertyNameCaseInsensitive = true`
- Comments and trailing commas allowed on read
- Property names are camelCase via `[JsonPropertyName]` attributes

### DatasetDialog

A modal dialog for loading existing training sets:
- Lists all saved training sets from the store
- Shows summary info: bonding point count, reference point count, frame validity
- Displays the full file path
- "Open Folder" button opens the trainer directory in Explorer

---

## UI Layout

The TrainerPanel uses a 3-column layout:

```
+------------------+---------------------------+-------------------+
| Left Sidebar     | Center Content            | Right Panel       |
| (200px fixed)    | (flexible)                | (280px fixed)     |
+------------------+---------------------------+-------------------+
| [Nav: Trainer]   | Step 1/2/3 wizard area    | Live Position     |
|                  |   - Start view            | Dataset info      |
| Step buttons:    |   - Capture view          | Frame status      |
|  [1] Bond Pos    |   - Review view           | Activity log      |
|  [2] Reference   |                           |                   |
|  [3] Retrace     | Data grid (bottom)        |                   |
|                  |   Step 1: bonding points   |                   |
| Device combo     |   Step 2: reference points |                   |
| Camera combo     |   Step 3: corrected pos    |                   |
+------------------+---------------------------+-------------------+
```

### Step Indicators

Each step button has a small circular indicator:
- **Gray** - Step not yet complete
- **Green** - Step requirements met
  - Step 1: >= 1 valid bonding point
  - Step 2: >= 2 valid reference points

### Live Position Display

A 200ms `DispatcherTimer` polls the selected motion device and displays current X/Y/Z coordinates. This updates in both the right panel header and within capture views.

### Step Navigation Guards

Switching steps while a capture is in progress triggers a confirmation dialog. If confirmed, the in-progress capture state (corners, images) is discarded.

---

## File Locations

| Item | Path |
|---|---|
| Training set JSON files | `%APPDATA%\Horizon\trainer\{name}.json` |
| Captured images | `%APPDATA%\Horizon\trainer\images\{uid}.bmp` |
| Trainer panel icon | `src/Horizon.App/Assets/Trainer.png` |

---

## Source Files

| File | Layer | Description |
|---|---|---|
| `src/Horizon.Core/TrainerModels.cs` | Core | Record types: `BondingCorner`, `BondingPoint`, `ReferenceCorner`, `ReferencePoint`, `TrainingSet` |
| `src/Horizon.Core/TrainerMath.cs` | Core | Pure math: frame construction, coordinate transforms, scale, center computation |
| `src/Horizon.Core/TrainerStore.cs` | Core | JSON persistence: load, save, delete, list training sets |
| `src/Horizon.App/TrainerPanel.xaml` | App | WPF layout: 3-step wizard, data grids, capture views |
| `src/Horizon.App/TrainerPanel.xaml.cs` | App | Wizard logic, hardware interaction, UI state management |
| `src/Horizon.App/DatasetDialog.xaml` | App | Modal dialog for selecting/browsing training sets |
| `src/Horizon.App/DatasetDialog.xaml.cs` | App | Dataset list, summary info, folder access |

---

## Example: End-to-End Flow

```
1. Operator creates "ProductA-Rev3" training set
2. Step 1: Teaches 4 bonding positions (2-corner each)
   - Bond1: left-pad   corners at (10.2, 5.1) and (10.8, 5.1) -> center (10.5, 5.1)
   - Bond2: right-pad  corners at (20.2, 5.0) and (20.8, 5.0) -> center (20.5, 5.0)
   - Bond3: top-pad    corners at (15.1, 10.2) and (15.7, 10.2) -> center (15.4, 10.2)
   - Bond4: bottom-pad corners at (15.0, 0.1) and (15.6, 0.1) -> center (15.3, 0.1)

3. Step 2: Teaches 2 reference points (fiducial marks on substrate)
   - Ref1: corners at (5.0, 2.5) and (5.0, 3.5) -> center (5.0, 3.0)
   - Ref2: corners at (25.0, 2.5) and (25.0, 3.5) -> center (25.0, 3.0)
   - Compute Frame: origin=(5.0, 3.0), xAxis=(1.0, 0.0), yAxis=(0.0, 1.0)
   - All bonding points converted to local coords relative to this frame

4. Step 3: New substrate loaded, slightly shifted and rotated
   - Move to Ref1 taught position, operator jogs to actual fiducial
   - Accept Ref1 at (5.12, 3.05)
   - Move to Ref2 taught position, operator jogs to actual fiducial
   - Accept Ref2 at (25.14, 3.10)
   - System computes:
     - New frame: origin=(5.12, 3.05), rotated ~0.14 deg
     - Scale: 1.000100 (20.0100 / 20.0000)
     - Corrected Bond1: (10.62, 5.16) -- delta (+0.12, +0.06)
     - Corrected Bond2: (20.62, 5.06) -- delta (+0.12, +0.06)
     - ... etc.
   - Operator clicks "Go" on each corrected position to verify
```

---

## Limitations and Notes

- **Z is not corrected** - Only X/Y are transformed; Z is preserved from the original teaching
- **2-reference minimum** - The frame requires exactly 2 reference points for axis computation. Additional references are taught but only Ref1 and Ref2 are used for the frame
- **No rotation-only mode** - Scale is always computed even if only rotation/translation occurred
- **1500ms settle delay** - Hard-coded delay after auto-move to center before image capture; may need tuning for different motion systems
- **Single motion device** - Each training set is bound to one motion device selected at creation time
- **Image format** - Camera snapshots are saved as `.bmp` (uncompressed); consider disk usage for large datasets
