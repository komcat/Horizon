# Horizon.Core

Central aggregation layer providing the DI container (`AppContext`), measurement data hub, teach point system, scanning engine, connection orchestration, and logging abstraction.

## Dependencies

References **all domain modules**:
- Horizon.Common, Horizon.Camera, Horizon.IO, Horizon.Motion, Horizon.Pneumatic
- Horizon.Process, Horizon.Safety, Horizon.CurrentMeasurement, Horizon.LaserController

## Files

| File | Description |
|------|-------------|
| `AppContext.cs` | DI container: creates, owns, and disposes all managers |
| `IAppContext.cs` | Public interface exposing all managers |
| `ILogSink.cs` | Logging abstraction (4 levels) |
| `MeasurementHub.cs` | Central pub/sub for timestamped measurement data |
| `ConnectionPool.cs` | Phased async hardware connection |
| `TeachPointManager.cs` | Product-based named position management |
| `TeachPointStore.cs` | JSON persistence for teach point data |
| `Scanning/ScanParameters.cs` | Configuration for coarse-to-fine scanning |
| `Scanning/ScanEngine.cs` | Coarse-to-fine hexapod scan algorithm |
| `Scanning/ScanningCommandHandler.cs` | Process engine handler for scan commands |

## AppContext

Sealed class implementing `IAppContext` and `IDisposable`. The central DI container that creates, owns, and orchestrates all subsystem managers.

### Owned Managers

| Property | Type | IDisposable |
|----------|------|-------------|
| `CameraManager` | `CameraManager` | Yes |
| `MotionManager` | `UnifiedMotionManager` | No |
| `IOManager` | `EziIOManager` | Yes |
| `PneumaticManager` | `PneumaticManager` | No |
| `CurrentMeasurementManager` | `CurrentMeasurementManager` | Yes |
| `LaserControllerManager` | `LaserControllerManager` | Yes |
| `ProcessEngine` | `ProcessEngine` | No |
| `PluginRegistry` | `ProcessPluginRegistry` | No |
| `SafetyWatchManager` | `SafetyWatchManager?` | Yes |
| `MeasurementHub` | `MeasurementHub` | No |
| `TeachPointManager` | `TeachPointManager` | No |
| `ConfigStore` | `CameraConfigStore` | No |

### Additional Properties

- `IsInitialized` — `true` after `Initialize()` completes (checks `_cameraManager != null`)
- `SimulationMode` — flag for simulation (not yet implemented)
- `LogSink` — set by `App.xaml.cs` (SerilogLogSink adapter)

### Initialize()

See [architecture.md](../architecture.md#step-3-appcontextinitialize-10-step-manager-creation) for the full 12-step initialization sequence.

### Shutdown()

Disposes managers in reverse order:
1. SafetyWatchManager.Dispose()
2. TeachPointManager = null
3. ProcessEngine = null, PluginRegistry = null
4. PneumaticManager = null
5. LaserControllerManager.Dispose()
6. CurrentMeasurementManager.Dispose()
7. UnifiedMotionManager.DisconnectAll()
8. EziIOManager.Dispose()
9. CameraManager: stop all grabbing → Dispose()

### Reinitialize()

Calls `Initialize()` which internally calls `Shutdown()` first, then re-creates everything.

## IAppContext

Public interface for dependency injection:

```csharp
public interface IAppContext
{
    CameraManager CameraManager { get; }
    UnifiedMotionManager MotionManager { get; }
    EziIOManager IOManager { get; }
    PneumaticManager PneumaticManager { get; }
    CurrentMeasurementManager CurrentMeasurementManager { get; }
    LaserControllerManager LaserControllerManager { get; }
    ProcessEngine ProcessEngine { get; }
    ProcessPluginRegistry PluginRegistry { get; }
    SafetyWatchManager? SafetyWatchManager { get; }
    MeasurementHub MeasurementHub { get; }
    TeachPointManager TeachPointManager { get; }
    bool IsInitialized { get; }
    bool SimulationMode { get; set; }
    void Initialize();
    void Reinitialize();
}
```

## ILogSink

```csharp
public enum LogLevel { Debug, Info, Warning, Error }

public interface ILogSink
{
    void Log(LogLevel level, string message);
}

// Extension methods
sink.Debug(message);
sink.Info(message);
sink.Warn(message);
sink.Error(message);
```

Implemented by `SerilogLogSink` in `Horizon.App` — bridges to Serilog's `Log.Debug/Information/Warning/Error`.

## MeasurementHub

Central pub/sub registry for timestamped measurement data.

### MeasurementChannel

Per-channel lock-based circular buffer:
- Capacity: 500 samples (configurable)
- Stores `(relativeTimestamp, value)` pairs
- Origin set on first `Push()` — timestamps are relative seconds
- `Snapshot(outTimestamps, outValues)` — copies buffer in chronological order to pre-allocated arrays (zero-GC)

### Hub Operations

| Method | Description |
|--------|-------------|
| `Push(key, value, timestamp)` | Push data to channel (auto-creates if new) |
| `GetChannel(key)` | Get channel or null |
| `GetOrCreateChannel(key)` | Get or create channel |
| `GetChannelsForDevice(deviceName)` | Filter by `"{deviceName}."` prefix |
| `GetAllChannelKeys()` | List all registered keys |
| `Clear()` | Clear all channels and data |

### Channel Key Convention

`"{DeviceName}.CH{N}"` — e.g., `"Keithley-1.CH1"`, `"Keithley-1.CH2"`

For laser controllers: `"{DeviceName}.Temperature"`, `"{DeviceName}.LaserCurrent"`

### Producers and Consumers

**Producers:** CurrentMeasurementPanel, LaserControllerPanel — push readings on poll timer

**Consumers:** CurrentReadingChart, CurrentChartPanel, OverlayWindow, LiveFeedPanel, ScanEngine — snapshot on render timer

Hub is NOT cleared on shutdown — charts retain history during reconnect.

## ConnectionPool

Phased async hardware connection with progress reporting.

### Phases

| Phase | What Connects | Parallelism |
|-------|---------------|-------------|
| 0 | Cameras | Parallel (all cameras) |
| 1 | IO devices + Motion controllers | Parallel (all devices) |
| 2 | Safety watch starts | Sequential |

Per-task timeout configurable (default 10s). `WhenAllWithTimeout()` races `Task.WhenAll` against `Task.Delay`.

Event: `OnProgress(string label, int done, int total)`

> Note: `ConnectionPool` is defined but the actual startup uses `SplashWindow`'s sequential connection loop instead.

## TeachPointManager

Product-based named position system. Each product contains a set of teach points (device + position name + 6-axis coordinates).

### Data Model

```
TeachPointProduct
  ├── ProductName: string
  └── Points: List<TeachPoint>
        ├── DeviceName: string
        ├── PositionName: string
        └── X, Y, Z, U, V, W: double
```

### Key Operations

| Method | Description |
|--------|-------------|
| `Load()` | Load all products from disk |
| `SetActiveProduct(name)` | Activate product + apply to motion manager |
| `ApplyActiveProduct()` | Push teach points to `UnifiedMotionManager.SetTeachPosition()` |
| `TeachCurrentPosition(product, device, name)` | Capture live position as teach point |
| `CreatePointManual(...)` | Add point with explicit coordinates |
| `MoveToPoint(device, name)` | Move to teach position (non-blocking) |
| `DeleteProduct(name)` | Remove product + clear motion presets if active |

### Persistence (TeachPointStore)

JSON files in `%APPDATA%\Horizon\teach-points\`:
- One JSON file per product (e.g., `MyProduct.json`)
- `active.json` tracks the active product name
- File operations silently catch exceptions

### Integration with Motion

When a product is activated:
1. `ClearTeachPositions()` on motion manager
2. For each point, call `SetTeachPosition(device, name, position)`
3. Teach positions override config-defined named positions in `UnifiedMotionManager`

## Scanning System

Coarse-to-fine hexapod scanning for peak finding.

### ScanParameters

Configuration with presets:
- `StepSizes`: array from coarse to fine (e.g., `[0.005, 0.002, 0.001, 0.0005]`)
- `Axes`: scan axes (e.g., `["Z", "X", "Y"]`)
- `SettleTimeMs`: wait after move before measurement
- `ConsecutiveDecreasesLimit`: early stop threshold
- `ExitThreshold`: optional — stop when measurement exceeds value
- Static builders: `Coarse()`, `Fine()`

### ScanEngine

Executes coarse-to-fine scan:
```
For each step size:
  For each axis:
    ScanDirection(+1) → record peak
    ScanDirection(-1) → record peak
    Return to best position
```

Measurements averaged over N samples at 50ms intervals. Early termination on:
- Exit threshold exceeded
- Significant drop (>10% from peak)
- Consecutive decreases limit reached
- MaxTotalDistanceMm constraint

Events: `ProgressChanged`, `DataPointRecorded`, `StatusChanged`

### ScanningCommandHandler

Process engine handler for `"scanning"` manager type:
- Method `hexscan`: parses parameters, creates `ScanEngine`, executes, stores results in `ProcessContext` (`hexscan.peak`, `hexscan.baseline`, `hexscan.peak_x/y/z`, etc.)

## Thread Model

- `AppContext`: created/disposed on UI thread; `Initialize()` runs on thread pool via `Task.Run`
- `MeasurementHub`: `ConcurrentDictionary` for channel registry; per-channel `lock` for buffer access
- `TeachPointManager`: single-threaded (UI thread)
- `ConnectionPool`: `Task.Run` for parallel hardware connections
- `ScanEngine`: synchronous execution (blocks calling thread with `Thread.Sleep`)
