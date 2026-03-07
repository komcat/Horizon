# Horizon.Common

Foundation library providing shared error handling, result types, and configuration loading. Has **zero project references** — every other project depends on this one.

## Files

| File | Description |
|------|-------------|
| `ErrorCode.cs` | `ushort` enum with 50+ error codes organized by hex category |
| `ErrorCategory.cs` | `byte` enum categorizing errors (General, Motion, IO, Pneumatic, Camera, CurrentMeasurement, LaserController, Process, Safety) |
| `Result.cs` | Generic discriminated union for error handling |
| `ConfigLoader.cs` | Static JSON config loader with local override pattern |
| `ClickToMoveConfig.cs` | Camera-to-motion pixel mapping configuration |

## ErrorCode System

Error codes are `ushort` values with the high byte indicating the category:

```
0x00xx  General
0x01xx  Motion
0x02xx  IO
0x03xx  Pneumatic
0x04xx  Camera
0x05xx  CurrentMeasurement
0x06xx  LaserController
0x09xx  Process
0x0Fxx  Safety
```

Extension methods:
- `GetCategory()` — extracts the `ErrorCategory` from the high byte
- `ToDisplayString()` — formatted output string

## Result\<T\>

Immutable struct for operation outcomes:

```csharp
var ok = Result<double>.Ok(3.14);
var fail = Result<double>.Fail(ErrorCode.MotionTimeout, "Axis X timed out");

if (result.IsSuccess)
    Console.WriteLine(result.Value);
else
    Console.WriteLine($"{result.Error}: {result.Message}");
```

## ConfigLoader

Static utility loading JSON configs from `%APPDATA%\Horizon\config\`.

### Load Logic

```
ConfigLoader.LoadJson<T>(basePath, name)
  1. Check {name}.local.json — if exists, load it
  2. Otherwise load {name}.json
  3. Deserialize with System.Text.Json
  4. Return default(T) if file not found
```

### JSON Options

- `PropertyNameCaseInsensitive = true`
- `ReadCommentHandling = JsonCommentHandling.Skip`
- `AllowTrailingCommas = true`

### `GetConfigDirectory()`

Returns `%APPDATA%\Horizon\config\` — the standard config directory used by all loaders.

### Local Override Pattern

Every config file supports a `.local.json` override. For example, `io_devices.local.json` takes priority over `io_devices.json`. This allows machine-specific configuration without modifying the base files.

## ClickToMoveConfig

Configuration for camera-to-motion coordinate mapping used by `OverlayWindow`:

- `CameraCalibration` class with pixel-to-mm conversion, magnification, axis inversion flags
- Per-camera calibration dictionary keyed by camera ID
- `GetCalibration(cameraId?)` method with fallback chain (per-camera → legacy top-level → defaults)
- Loaded from `click_to_move.json`

## Thread Model

All types are stateless static utilities or immutable value types — no thread safety concerns.
