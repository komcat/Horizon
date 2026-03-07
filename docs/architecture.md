# Horizon Architecture Documentation

Comprehensive reference for the Horizon modular hardware control system — a WPF + AvalonDock application for cameras, motion stages, digital I/O, pneumatic slides, current measurement, laser controllers, process sequencing, and safety monitoring.

---

## 1. Overview & Module Map

**Solution:** `Horizon.sln` — 11 projects, all targeting `net8.0` (x64 only)

| # | Project | Description |
|---|---------|-------------|
| 1 | **[Horizon.Common](modules/common.md)** | Foundation: `ErrorCode`, `Result<T>`, `ConfigLoader` |
| 2 | **[Horizon.Camera](modules/camera.md)** | Camera adapters (Basler Pylon, IDS Peak, IDS uEye) |
| 3 | **[Horizon.IO](modules/io.md)** | Fastech Ezi-IO digital I/O via P/Invoke |
| 4 | **[Horizon.Motion](modules/motion.md)** | ACS SPiiPlus + PI GCS2 motion controllers |
| 5 | **[Horizon.Pneumatic](modules/pneumatic.md)** | Pneumatic solenoid slides (drives IO output pins) |
| 6 | **[Horizon.Safety](modules/safety.md)** | IO input edge-detection safety watchdog |
| 7 | **[Horizon.CurrentMeasurement](modules/current-measurement.md)** | Keithley 6482 picoammeter via NI-VISA |
| 8 | **[Horizon.LaserController](modules/laser-controller.md)** | Thorlabs CLD101x laser diode + TEC via NI-VISA |
| 9 | **[Horizon.Process](modules/process.md)** | ProcessEngine, CommandDispatcher, plugin system |
| 10 | **[Horizon.Core](modules/core.md)** | AppContext (DI container), MeasurementHub, TeachPointManager |
| 11 | **[Horizon.App](modules/app.md)** | WPF shell with AvalonDock panel system |

### Dependency Graph

```
Horizon.App  (WPF entry point)
 ├── Horizon.Core
 │    ├── Horizon.Common
 │    ├── Horizon.Camera ──────── Horizon.Common
 │    ├── Horizon.IO ──────────── Horizon.Common
 │    ├── Horizon.Motion ──────── Horizon.Common
 │    ├── Horizon.Pneumatic ───── Horizon.Common + Horizon.IO
 │    ├── Horizon.Safety ──────── Horizon.Common + Horizon.IO
 │    ├── Horizon.CurrentMeasurement ── Horizon.Common
 │    ├── Horizon.LaserController ───── Horizon.Common
 │    └── Horizon.Process ─────── Horizon.Common
 ├── Horizon.Process
 ├── Horizon.IO
 ├── Horizon.Motion
 ├── Horizon.Pneumatic
 ├── Horizon.CurrentMeasurement
 └── Horizon.LaserController
```

> `Horizon.Common` has zero project references — it is the leaf dependency for every other project.

---

## 2. Module Relationships

### 2.1 Horizon.Common

**References:** none
**Key types:** `ErrorCode` (hex-categorized enum), `Result<T>`, `ConfigLoader`
**Consumed by:** every other project
**Thread model:** all static utilities, no state

`ConfigLoader.LoadJson<T>(basePath, name)` loads `{name}.local.json` first, falls back to `{name}.json`. Uses `System.Text.Json` with `PropertyNameCaseInsensitive`, `ReadCommentHandling.Skip`, and `AllowTrailingCommas`.

### 2.2 Horizon.Camera

**References:** Horizon.Common
**Exposes:** `ICameraHardware`, `CameraManager`
**Adapters:** `PylonCameraAdapter` (Basler.Pylon managed), `IDSPeakCameraAdapter` (ids_peak_dotnet), `UEyeCameraAdapter` (uEye P/Invoke)
**Thread model:** `CameraManager` uses `ConcurrentDictionary` for camera registry; frame callbacks fire on SDK threads

`ICameraHardware` key methods:
- `Initialize()`, `Connect()`, `Disconnect()`, `StartGrabbing()`, `StopGrabbing()`
- `CaptureFrame(CameraFrameData)`, `SetExposureSettings()`, `GetExposureSettings()`
- `FrameCallback` delegate for broadcast system
- Status: `IsConnected`, `IsGrabbing`, `IsDeviceRemoved`

`CameraManager` implements `IDisposable`: `Dispose()` stops broadcast system, disposes all `ICameraHardware` instances, clears collections.

### 2.3 Horizon.IO

**References:** Horizon.Common
**Exposes:** `IIODevice`, `EziIOManager`, `EziIODevice`
**SDK:** `EziMOTIONPlusE.dll` via P/Invoke (`Native/EziNative.cs`)
**Thread model:** `EziIOManager` uses `PeriodicTimer` for async input polling; `lock` guards hardware calls

`IIODevice` key methods:
- `Connect()`, `Disconnect()`, `IsConnected`
- `ReadInputs(out uint)`, `ReadInputs(out uint inputs, out uint latch)`
- `SetOutput(int pin, bool state)`, `SetOutputByName(string pinName, bool state)`
- `ReadAllOutputs(out uint)`, `SetPinName()`, `GetPinByName()`
- Standalone Ezi-IO: combined register with bit offset (inputs at `[0..InputCount-1]`, outputs at `[InputCount..InputCount+OutputCount-1]`)

`EziIOManager` implements `IDisposable`: `Dispose()` stops polling timer, disconnects all devices.

### 2.4 Horizon.Motion

**References:** Horizon.Common
**Exposes:** `IMotionController`, `UnifiedMotionManager`
**Adapters:** `ACSMotionController` (ACS.SPiiPlusNET6 managed, TCP 701), `PIMotionController` (PI_GCS2_DLL_x64 P/Invoke)
**Thread model:** `lock` guards per-controller hardware access

`IMotionController` key methods:
- `Connect()`, `Disconnect()`, `IsConnected`
- `MoveToPosition(axis, position, blocking)`, `MoveRelative(axis, distance, blocking)`
- `IsMoving(axis)`, `IsOnTarget(axis)`, `IsDeviceInMotion()`
- `GetCurrentPosition()` returns `PositionStruct`
- `HomeAxis(axis)`, `StopAxis(axis)`, `StopAll()`

`UnifiedMotionManager` does NOT implement `IDisposable` — cleanup is via `DisconnectAll()` which calls `DisconnectAll()` on internal sub-managers.

### 2.5 Horizon.Pneumatic

**References:** Horizon.Common, **Horizon.IO**
**Exposes:** `IPneumaticSlide`, `PneumaticManager`
**Thread model:** delegates to `IIODevice` (inherits IO thread safety)

`IPneumaticSlide` key methods:
- `Extend()`, `Retract()`, `ExtendAndWait(pollIntervalMs)`, `RetractAndWait(pollIntervalMs)`
- `GetStatus()` returns `PneumaticStatus`, `GetPosition()` returns `PneumaticPosition`

**Critical dependency:** `PneumaticSlide` drives solenoid valves via `EziIOManager` output pins. IO manager must be initialized before pneumatic slides are loaded. `PneumaticJsonLoader.LoadSlides()` takes `EziIOManager` as a parameter to resolve device references.

`PneumaticManager` does NOT implement `IDisposable` — has `StopAll()` to retract all slides.

### 2.6 Horizon.Safety

**References:** Horizon.Common, **Horizon.IO**
**Exposes:** `SafetyWatchManager`, `SafetyEventLog`
**Thread model:** `PeriodicTimer`-based async poll loop (default 50ms)

`SafetyWatchManager` polls configured IO input pins for edge transitions (Rising, Falling, Both). On trigger, sets `IsSafetyFlagRaised` / `IsCriticalFlagRaised` and fires `SafetyTriggered` event.

**Critical dependency:** requires connected IO devices. Safety watch starts in Phase 2 of the connection sequence (after IO devices are connected in Phase 1).

Implements `IDisposable`: `Dispose()` calls `StopWatching()` which cancels the poll timer and waits up to 2 seconds for the watch task.

### 2.7 Horizon.CurrentMeasurement

**References:** Horizon.Common
**Exposes:** `ICurrentMeasurementDevice`, `CurrentMeasurementManager`
**Adapters:** `Keithley6482Adapter` via `visa64.dll` P/Invoke (`Native/VisaNative.cs`)
**Thread model:** `PeriodicTimer` for polling; `lock` guards VISA session

`ICurrentMeasurementDevice` key methods:
- `Connect()`, `Disconnect()`, `IsConnected`
- `ReadCurrent(channel, out double)`, `ReadAllChannels(out double[])`
- `SetVoltage(volts)`, `GetVoltage(out volts)`, `SetVoltageEnabled(bool)`
- `ConfigureFastMode()`, `ConfigureDefaultMode()`
- `GetIdentity(out string)`, `Reset()`

`CurrentMeasurementManager` implements `IDisposable`: `Dispose()` stops polling, disconnects all devices.

### 2.8 Horizon.LaserController

**References:** Horizon.Common
**Exposes:** `ILaserControllerDevice`, `LaserControllerManager`
**Adapters:** `CLD101xAdapter` via `visa64.dll` P/Invoke (SCPI over USB)
**Thread model:** `lock` guards VISA session

`ILaserControllerDevice` key methods:
- `Connect()`, `Disconnect()`, `IsConnected`
- TEC: `SetTemperature()`, `ReadTemperature()`, `TECOn()`, `TECOff()`, `IsTECOn`
- Laser: `SetLaserCurrent()`, `ReadLaserCurrent()`, `LaserOn()`, `LaserOff()`, `IsLaserOn`
- `GetStatus()` returns `LaserControllerStatus`

`LaserControllerManager` implements `IDisposable`: `Dispose()` disconnects all devices.

### 2.9 Horizon.Process

**References:** Horizon.Common
**Exposes:** `ProcessEngine`, `CommandDispatcher`, `ProcessContext`, `ProcessPluginRegistry`, `ProcessBuilder`, `ExpressionEvaluator`
**Interfaces:** `ICommandHandler`, `IProcessPlugin`, `PluginParamDef`
**Thread model:** `ProcessEngine` uses exclusive lock (`TryAcquire`/`Release`) for single execution; `ProcessContext` is a thread-safe `ConcurrentDictionary`

**8 command handlers:** motion, io, pneumatic, camera, currentmeasurement, lasercontroller, system, context

`ProcessEngine` builds its device registry via reflection on `AppContext` properties — iterates all public properties of `IAppContext`, finds managers, and registers their devices. Supports actions, loops (`BeginLoop`/`EndLoop`), conditionals (`BeginIf`/`Else`/`EndIf`), and step markers. Default step timeout: 60 seconds.

`ExpressionEvaluator` — `ref struct` recursive-descent parser on `ReadOnlySpan<char>`. Operators: `+`, `-`, `*`, `/` with proper precedence, parentheses, float literals (scientific notation), variable references from `ProcessContext`. Division by zero returns IEEE 754 Infinity.

### 2.10 Horizon.Core

**References:** Horizon.Common, Horizon.Camera, Horizon.IO, Horizon.Motion, Horizon.Pneumatic, Horizon.Process, Horizon.Safety, Horizon.CurrentMeasurement, Horizon.LaserController
**Exposes:** `AppContext` (implements `IAppContext`, `IDisposable`), `IAppContext`, `ILogSink`, `MeasurementHub`, `TeachPointManager`, `ConnectionPool`
**Thread model:** `AppContext` is single-threaded (created/disposed on UI thread); `MeasurementHub` uses `ConcurrentDictionary` + per-channel `lock`

`AppContext` is the DI container — owns all manager instances, orchestrates their creation in dependency order, and tears them down in reverse order.

`IAppContext` exposes: `CameraManager`, `MotionManager`, `IOManager`, `PneumaticManager`, `CurrentMeasurementManager`, `LaserControllerManager`, `ProcessEngine`, `PluginRegistry`, `SafetyWatchManager?`, `MeasurementHub`, `TeachPointManager`, `IsInitialized`, `SimulationMode`.

`MeasurementHub` — central pub/sub. Channels auto-created on first `Push(key, value, timestamp)`. Each `MeasurementChannel` is a lock-based circular buffer (500 samples). Channel key convention: `"{DeviceName}.CH{N}"`.

`TeachPointManager` — product-based named position system. Stores teach points per product as JSON files. Applies named positions to `UnifiedMotionManager` when a product is activated.

`ILogSink` — 4-level logging interface (`Debug`, `Info`, `Warning`, `Error`) with extension methods. Implemented by `SerilogLogSink` in `Horizon.App`.

### 2.11 Horizon.App

**References:** Horizon.Core, Horizon.Process, Horizon.IO, Horizon.Motion, Horizon.Pneumatic, Horizon.CurrentMeasurement, Horizon.LaserController
**Key types:** `App`, `SplashWindow`, `MainWindow`, 14+ UI panels
**Thread model:** WPF single UI thread; `DispatcherTimer` for panel polling; hardware calls dispatched to thread pool via `Task.Run()`

UI Panels: `IOPanel`, `MotionPanel`, `PneumaticPanel`, `CurrentMeasurementPanel`, `CurrentChartPanel`, `LaserControllerPanel`, `LiveFeedPanel`, `CameraInfoPanel`, `OverlayWindow`, `ProcessPanel`, `RunPanel`, `JogPanel`, `TeachPanel`, `HexScanPanel`

---

## 3. Application Startup Sequence

### Step 1: `App.OnStartup` (`App.xaml.cs`)

```
App.OnStartup(StartupEventArgs)
  1. Configure Serilog
     - Rolling daily log file: %APPDATA%\Horizon\logs\horizon-{date}.log
     - 7-day retention, Debug minimum level
     - WPF output sink (UiSink) at Information level
  2. InitializePylonEnvironment()
     - Find Pylon SDK via PYLON_ROOT env var or scan Program Files\Basler\pylon*
     - Set PYLON_ROOT, add Runtime\x64 to PATH
     - Set GENICAM_GENTL64_PATH for GigE transport layer
     - AddDllDirectory() for Windows DLL search
  3. Create AppContext instance
  4. Set AppContext.LogSink = new SerilogLogSink()
  5. Show SplashWindow
  6. await splash.RunConnectionsAsync()
  7. Show MainWindow, close SplashWindow
```

### Step 2: `SplashWindow.RunConnectionsAsync()`

```
RunConnectionsAsync()
  Step 1: await Task.Run(() => _context.Initialize())
    → Runs AppContext.Initialize() on thread pool (see Step 3)
  Step 2: BuildDeviceList()
    → Enumerates IO, Motion, CurrentMeasurement, Camera managers
    → Populates visual device list (Pending status)
  Step 3: Sequential device connection
    → For each device in list:
       - Set status to "Connecting..." (amber)
       - Yield to UI for render
       - await Task.Run(() => ConnectDevice(device))
       - Set status to "Connected" (green) or "Failed" (red)
    → Connection order: IO devices → Motion devices → CurrentMeasurement → Cameras
  Step 4: Show summary ("X connected, Y failed"), 800ms delay
```

### Step 3: `AppContext.Initialize()` (10-step manager creation)

```
Initialize()
  1. Shutdown()           — clean up any previous state
  2. ConfigLoader.GetConfigDirectory()  → %APPDATA%\Horizon\config\
  3. CameraManager        — create, discover Pylon cameras, discover IDS cameras,
                            load registered cameras from CameraConfigStore
  4. EziIOManager         — create, load devices from io_devices.json
  5. UnifiedMotionManager — create, load devices from motor_devices.json
  6. CurrentMeasurementManager — create, load from current_measurement_devices.json
  7. LaserControllerManager    — create, load from laser_controller_devices.json
  8. PneumaticManager     — create, load from pneumatic_slides.json
                            (receives EziIOManager reference for pin resolution)
  9. SafetyWatchManager   — create with (EziIOManager, SafetyEventLog),
                            load from safety_watch.json
 10. ProcessEngine        — create, BuildDeviceRegistry(this) via reflection,
                            register ScanningCommandHandler
 11. ProcessPluginRegistry — create, DiscoverPlugins() from Process assembly
 12. TeachPointManager    — create with (MotionManager), load teach point files,
                            apply active product
```

**Dependency order matters:**
- IO must be created before Pneumatic (step 4 before step 8)
- IO must be created before Safety (step 4 before step 9)
- All managers must exist before ProcessEngine builds device registry (step 10)
- MotionManager must exist before TeachPointManager (step 5 before step 12)

### Step 4: `MainWindow.Loaded`

```
MainWindow_Loaded()
  1. RestoreWindowState()   — restore window position/size from window-state.txt
  2. RestoreDockLayout()    — restore AvalonDock panel layout from dock-layout.xml
  3. Populate camera SDK/connection combos
  4. Wire WPF output sink   — App.UiSink.Initialize(Dispatcher)
  5. Bind OutputListBox      — auto-scrolling log display
  6. LoadRegisteredCameras() — populate camera config grid
  7. RefreshHardwareTree()   — populate TreeView with all connected devices
```

---

## 4. Application Shutdown Sequence

### Step 1: `MainWindow.Closing`

```
MainWindow_Closing()
  1. StopLiveFeed()                — stop camera live feed rendering
  2. StopAllIOPanels()             — stop DispatcherTimer in each IOPanel
  3. StopAllMotionPanels()         — stop DispatcherTimer in each MotionPanel
  4. StopAllPneumaticPanels()      — stop DispatcherTimer in each PneumaticPanel
  5. StopAllCurrentMeasPanels()    — stop DispatcherTimer in each CurrentMeasurementPanel
  6. StopAllCurrentChartPanels()   — stop chart update timers
  7. StopAllLaserControllerPanels() — stop polling in LaserControllerPanels
  8. StopAllProcessPanels()        — stop process execution panels
  9. StopAllRunPanels()            — stop RunPanel chain editors
 10. StopAllJogPanels()            — stop jog control panels
 11. StopAllTeachPanels()          — stop teach point panels
 12. StopAllHexScanPanels()        — stop hex scan panels
 13. Close OverlayWindow           — _overlayWindow?.Close()
 14. SaveWindowState()             — persist window position/size
 15. SaveDockLayout()              — persist AvalonDock layout to XML
```

Each `StopAll*` method finds LayoutDocuments by ContentId prefix, casts to the panel type, and calls `StopPolling()`.

**Panel polling must stop before manager disposal** — panels hold references to managers and poll on DispatcherTimer. If managers dispose first, polling would hit disposed objects.

### Step 2: `App.OnExit`

```
App.OnExit()
  1. _appContext?.Dispose()  → calls AppContext.Shutdown()
  2. Log.CloseAndFlush()     → flush and close Serilog
```

### Step 3: `AppContext.Shutdown()` (reverse-order disposal)

```
Shutdown()
  1. SafetyWatchManager.Dispose()
     → StopWatching(): cancel PeriodicTimer, wait up to 2s for watch task
  2. TeachPointManager = null              (no cleanup needed)
  3. ProcessEngine = null                  (no cleanup needed)
  4. PluginRegistry = null                 (no cleanup needed)
  5. PneumaticManager = null               (no active resources)
  6. LaserControllerManager.Dispose()
     → DisconnectAll(): disconnect each VISA session
  7. CurrentMeasurementManager.Dispose()
     → StopPolling(): cancel PeriodicTimer, wait for poll task
     → DisconnectAll(): close each VISA session
  8. UnifiedMotionManager.DisconnectAll()
     → Disconnect each ACS/PI controller
  9. EziIOManager.Dispose()
     → StopPolling(): cancel PeriodicTimer, wait for poll task
     → DisconnectAll(): disconnect each Ezi-IO device
 10. CameraManager
     → Stop grabbing on all cameras
     → CameraManager.Dispose(): stop broadcast, dispose all ICameraHardware, clear
```

**Shutdown order is the reverse of initialization order:** Safety → TeachPoints → Process → Pneumatic → Laser → CurrentMeas → Motion → IO → Camera.

### IDisposable Summary

| Manager | IDisposable | Cleanup Method |
|---------|-------------|----------------|
| CameraManager | Yes | `Dispose()` — stop broadcast, dispose all hardware |
| EziIOManager | Yes | `Dispose()` — stop polling, disconnect all |
| UnifiedMotionManager | No | `DisconnectAll()` — disconnect sub-managers |
| PneumaticManager | No | `StopAll()` — retract all slides (not called in shutdown) |
| CurrentMeasurementManager | Yes | `Dispose()` — stop polling, disconnect all |
| LaserControllerManager | Yes | `Dispose()` — disconnect all |
| SafetyWatchManager | Yes | `Dispose()` — stop watching |

---

## 5. Runtime Data Flow

### 5.1 Config Loading Pattern

```
%APPDATA%\Horizon\config\
  ├── io_devices.json           (IO device definitions)
  ├── io_devices.local.json     (local override, gitignored)
  ├── motor_devices.json        (motion controller definitions)
  ├── pneumatic_slides.json     (slide-to-pin mappings)
  ├── safety_watch.json         (watched inputs + edge triggers)
  ├── current_measurement_devices.json
  └── laser_controller_devices.json
```

`ConfigLoader.LoadJson<T>(basePath, name)`:
1. Check `{name}.local.json` — if exists, load it
2. Otherwise load `{name}.json`
3. Deserialize with `System.Text.Json` (case-insensitive, comments allowed, trailing commas allowed)
4. Return `default(T)` if file not found

Each manager has a corresponding `*JsonLoader` static class (e.g., `EziIOJsonLoader`, `MotionJsonLoader`) that calls `ConfigLoader` and populates the manager.

### 5.2 MeasurementHub Pub/Sub

```
Producer (e.g., CurrentMeasurementPanel)
    │
    │  hub.Push("Keithley-1.CH1", 1.23e-9, DateTimeOffset.Now)
    │
    ▼
MeasurementHub
    │  ConcurrentDictionary<string, MeasurementChannel>
    │  Auto-creates channel on first Push
    │
    ▼
MeasurementChannel ("Keithley-1.CH1")
    │  lock-based circular buffer (500 samples)
    │  Stores (relativeTimestamp, value) pairs
    │
    ▼
Consumer (e.g., CurrentChartPanel, OverlayWindow)
    │  channel.Snapshot(timestamps, values) → copy to pre-allocated arrays
    │  SkiaSharp rendering on DispatcherTimer
```

Channel key convention: `"{DeviceName}.CH{N}"` (e.g., `"Keithley-1.CH1"`, `"Keithley-1.CH2"`)

Hub is NOT cleared on shutdown — charts retain history during reconnect cycles.

### 5.3 Process Execution Pipeline

```
ProcessProgram (.hproc JSON file)
    │
    │  User selects plugin from catalog, configures parameters
    │
    ▼
IProcessPlugin.BuildSteps(ProcessBuilder)
    │  Plugin emits steps: AddAction(), BeginLoop(), BeginIf(), etc.
    │
    ▼
ProcessBuilder.Build() → List<ProcessStep>
    │
    ▼
ProcessEngine.ExecuteAsync(steps)
    │  TryAcquire exclusive lock
    │  For each step:
    │    1. OnStepStarted callback
    │    2. Check breakpoints (ManualResetEventSlim gate)
    │    3. Route via CommandDispatcher
    │    4. OnStepCompleted callback
    │
    ▼
CommandDispatcher.Dispatch(managerType, ...)
    │  Lookup ICommandHandler by type string
    │  (motion, io, pneumatic, camera, currentmeasurement,
    │   lasercontroller, system, context)
    │
    ▼
ICommandHandler.ExecuteCommand(manager, device, method, params, context)
    │  Calls actual hardware manager methods
    │  Returns (bool success, string error)
```

### 5.4 ProcessContext Variable Lifecycle

`ProcessContext` holds runtime variables as a thread-safe dictionary (`ConcurrentDictionary<string, string>`):

1. **Set by handlers:** `context.SetValue("result", "123.45")` — any handler can store results
2. **Read by handlers:** `context.GetNumeric("distance")`, `context.GetString("filename")`, `context.GetBoolean("flag")`
3. **Expression evaluation:** `ExpressionEvaluator` resolves variable names from context (e.g., `expr:posA - posB`)
4. **UI debugging:** `context.GetSnapshot()` returns sorted list of `(key, value, type)` triplets for the Context Watch DataGrid
5. **Lifetime:** created fresh per execution, discarded when execution completes

### 5.5 Expression Evaluator Usage Patterns

1. **CalculateExpressionPlugin** — standalone: evaluates expression string, stores result in named context variable
2. **Parameter prefix** — inline in any handler parameter: `expr:distance * 0.5` computes and substitutes the value
3. **Log interpolation** — in SystemCommandHandler log messages: `Distance = {=posA - posB}` evaluates and embeds the result

---

## 6. Interface Contract Summary

| Interface | Module | Key Methods | Adapters | SDK |
|-----------|--------|-------------|----------|-----|
| `ICameraHardware` | Camera | `Connect`, `StartGrabbing`, `CaptureFrame`, `SetExposureSettings`, `FrameCallback` | PylonCameraAdapter, IDSPeakCameraAdapter, UEyeCameraAdapter | Basler.Pylon (managed), ids_peak_dotnet (managed), uEye (P/Invoke) |
| `IMotionController` | Motion | `Connect`, `MoveToPosition`, `MoveRelative`, `HomeAxis`, `StopAll`, `GetCurrentPosition` | ACSMotionController, PIMotionController | ACS.SPiiPlusNET6 (managed, TCP 701), PI_GCS2_DLL_x64 (P/Invoke) |
| `IIODevice` | IO | `Connect`, `ReadInputs`, `SetOutput`, `SetOutputByName`, `ReadAllOutputs`, `GetPinByName` | EziIODevice | EziMOTIONPlusE.dll (P/Invoke) |
| `IPneumaticSlide` | Pneumatic | `Extend`, `Retract`, `ExtendAndWait`, `RetractAndWait`, `GetStatus`, `GetPosition` | PneumaticSlide | Uses IIODevice output pins |
| `ICurrentMeasurementDevice` | CurrentMeasurement | `Connect`, `ReadCurrent`, `ReadAllChannels`, `SetVoltage`, `ConfigureFastMode` | Keithley6482Adapter | visa64.dll P/Invoke (NI-VISA) |
| `ILaserControllerDevice` | LaserController | `Connect`, `SetTemperature`, `TECOn/Off`, `SetLaserCurrent`, `LaserOn/Off`, `GetStatus` | CLD101xAdapter | visa64.dll P/Invoke (NI-VISA, SCPI over USB) |
| `ILogSink` | Core | `Log(LogLevel, string)` + extension methods: `Debug`, `Info`, `Warn`, `Error` | SerilogLogSink (in App) | Serilog |

---

## 7. Critical Dependency Rules

### Initialization Order Constraints

1. **IO before Pneumatic** — `PneumaticJsonLoader.LoadSlides()` requires `EziIOManager` to resolve device/pin references
2. **IO before Safety** — `SafetyWatchManager` constructor requires `EziIOManager` for input polling
3. **All managers before ProcessEngine** — `BuildDeviceRegistry(this)` uses reflection to enumerate all `IAppContext` properties and register devices
4. **MotionManager before TeachPointManager** — `TeachPointManager` constructor requires `UnifiedMotionManager` for position operations

### Connection Phase Order

| Phase | What Connects | Why This Order |
|-------|---------------|----------------|
| Phase 0 | Cameras | Independent, can connect first |
| Phase 1 | IO devices + Motion controllers (parallel) | Required for Pneumatic and Safety |
| Phase 2 | Safety watch starts | Needs connected IO devices to poll inputs |

> Note: CurrentMeasurement and LaserController devices are connected during the SplashWindow sequential loop alongside IO and Motion. They do not have a dedicated phase in ConnectionPool but are connected by SplashWindow's `ConnectDevice()`.

### Shutdown Order Constraints

1. **Panel polling must stop before manager disposal** — `MainWindow_Closing` stops all 12 panel categories before `App.OnExit` disposes AppContext
2. **Safety watch stops first** — prevents safety triggers during hardware disconnection
3. **Pneumatic nulled before IO disposed** — pneumatic slides reference IO pins
4. **IO disposed last among hardware** — other subsystems may reference IO during their shutdown
5. **Camera disposed last** — stop grabbing on all cameras, then dispose hardware instances

### Thread Safety Rules

- `ConcurrentDictionary` for device collections (CameraManager, MeasurementHub)
- `lock` for individual hardware access (IO, Motion, VISA sessions)
- `PeriodicTimer` for async polling loops (IO, CurrentMeasurement, Safety)
- UI panels use `DispatcherTimer` — all UI updates on WPF dispatcher thread
- Hardware calls from UI panels dispatched via `Task.Run()` to avoid blocking UI
- `ProcessEngine` uses exclusive `TryAcquire`/`Release` lock — only one process execution at a time
- Pin names use `StringComparer.OrdinalIgnoreCase` throughout
