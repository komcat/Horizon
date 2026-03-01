# Horizon — Architecture Layers

This document describes the application's layered architecture from top-level initialization down to low-level hardware controllers.

**Platform:** .NET 8 (C#), WPF, x64 only | **Solution:** `Horizon.sln` (10 projects, 81 source files)

---

## Layer Diagram (Overview)

```
+-----------------------------------------------------------------------+
|                      Application Layer (WPF)                          |
|  App.xaml.cs -> SplashWindow (async connect) -> MainWindow (AvalonDock)|
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                    AppContext (DI Container)                           |
|  Horizon.Core.AppContext owns all managers, MeasurementHub            |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                    Data Hub Layer                                      |
|  MeasurementHub (per-channel circular buffers, pub/sub)               |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                    Process Execution Layer                             |
|  ProcessEngine -> CommandDispatcher -> ICommandHandler (7 handlers)   |
|  ProcessBuilder | ProcessContext | Breakpoint/StepByStep support      |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                        Manager Layer                                   |
|  CameraManager | UnifiedMotionManager | EziIOManager                  |
|  PneumaticManager | CurrentMeasurementManager | SafetyWatchManager    |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                   Hardware Abstraction Interfaces                      |
|  ICameraHardware | IMotionController | IIODevice                      |
|  IPneumaticSlide | ICurrentMeasurementDevice                          |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                      Adapter / Device Layer                            |
|  PylonCameraAdapter | IDSPeakCameraAdapter | ACSMotionController      |
|  PIMotionController | EziIODevice | PneumaticSlide | Keithley6482     |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                        SDK / Protocol Layer                            |
|  Basler Pylon | IDS Peak | ACS.SPiiPlusNET6 | PI GCS2 DLL           |
|  Fastech EziMOTION | NI-VISA (GPIB/USB for Keithley)                |
+-----------------------------------------------------------------------+
```

---

## 1. Application Layer

**Project:** `Horizon.App` (net8.0-windows, WPF, AllowUnsafeBlocks)

### Startup Sequence

```
App.xaml.cs
  |
  +-> Set Pylon environment (PYLON_ROOT, PATH, GENICAM_GENTL64_PATH, DLL search dirs)
  +-> Create AppContext
  +-> Show SplashWindow
  |     +-> AppContext.Initialize() on background thread
  |     +-> Sequential: Camera, IO, Motion, CurrentMeasurement, Pneumatic, Safety, Process
  |     +-> Parallel connect tasks per subsystem
  |     +-> Progress bar updates via Dispatcher
  +-> Show MainWindow (AvalonDock shell)
  +-> SplashWindow closes
```

### MainWindow (AvalonDock Shell)

The main window hosts a dockable panel layout with:

| Panel Type | Content ID Prefix | Description |
|---|---|---|
| IOPanel | `io_` | Per-device digital I/O pin state + toggle |
| MotionPanel | `motion_` | Per-device axis position + jog controls |
| PneumaticPanel | `pneu_` | Per-slide extend/retract + sensor state |
| CurrentMeasurementPanel | `cmeas_` | Per-device channel readings + fast poll |
| LiveFeedPanel | `liveFeed_` | Camera live video + chart overlay |
| CameraInfoPanel | `camInfo_` | Camera settings + exposure controls |
| CurrentChartPanel | `chart_` | Pop-out current chart with device selector |
| OverlayWindow | (separate window) | Camera + SkiaSharp chart overlay |

**Anchorables:** Hardware tree (left), Output log (bottom), Camera config (collapsible)

Panels are created on demand (double-click hardware tree item) and destroyed on close. Layout state (dock positions, window bounds) is persisted to `%APPDATA%\Horizon\dock-layout.xml` and `window-state.txt`.

### UI Data Flow (MeasurementHub)

```
              MeasurementHub (Horizon.Core, owned by AppContext)
              +---------------------------------------------+
 Producers:   |  ConcurrentDictionary<string, Channel>      |  Consumers:
              |                                             |
 CMeasPanel --Push--> "Keithley-1.CH1" (500-sample buffer) --> CurrentReadingChart
 CMeasPanel --Push--> "Keithley-1.CH2" (500-sample buffer) --> OverlayWindow
 (future)   --Push--> "PowerMeter.CH1"                     --> CurrentChartPanel
 (future)   --Push--> "TempSensor.T1"                      --> LiveFeedPanel overlay
              +---------------------------------------------+

 Device selector combos on OverlayWindow, CurrentChartPanel, and LiveFeedPanel
 allow users to switch which device's channels are displayed.
```

---

## 2. AppContext (DI Container)

**Files:** `Horizon.Core/AppContext.cs`, `Horizon.Core/IAppContext.cs`

### Interface

```csharp
public interface IAppContext
{
    CameraManager CameraManager { get; }
    UnifiedMotionManager MotionManager { get; }
    EziIOManager IOManager { get; }
    PneumaticManager PneumaticManager { get; }
    CurrentMeasurementManager CurrentMeasurementManager { get; }
    ProcessEngine ProcessEngine { get; }
    SafetyWatchManager? SafetyWatchManager { get; }
    MeasurementHub MeasurementHub { get; }
    bool IsInitialized { get; }
    bool SimulationMode { get; set; }
    void Initialize();
    void Reinitialize();
}
```

### Initialization Order

Managers are created sequentially in strict dependency order:

1. **CameraManager** — discover Pylon + IDS cameras, load registered config
2. **EziIOManager** — load `io_devices.json`
3. **UnifiedMotionManager** — load `motor_devices.json`
4. **CurrentMeasurementManager** — load `current_measurement_devices.json`
5. **PneumaticManager** — load `pneumatic_slides.json` (depends on IOManager)
6. **SafetyWatchManager** — load `safety_watch.json` (depends on IOManager)
7. **ProcessEngine** — `BuildDeviceRegistry(this)` via reflection

**MeasurementHub** is created once at construction (not cleared on Shutdown — charts retain history during reconnect).

### Shutdown

Managers are disposed in reverse order. Camera grabbing is stopped, IO devices disconnected, motion controllers disconnected, safety watch stopped.

---

## 3. Data Hub Layer

**File:** `Horizon.Core/MeasurementHub.cs`

### MeasurementChannel

Per-channel thread-safe circular buffer:

| Method | Description |
|---|---|
| `Push(double value, DateTimeOffset ts)` | Add timestamped sample (auto-creates time origin) |
| `Snapshot(double[] outTs, double[] outVals) → int` | Copy buffer to arrays (oldest-first), returns count |
| `Clear()` | Reset head/count/origin |

- Capacity: 500 samples (default)
- Thread safety: `lock` on all operations
- Timestamps: relative seconds from first push

### MeasurementHub

Central registry with `ConcurrentDictionary<string, MeasurementChannel>` (OrdinalIgnoreCase):

| Method | Description |
|---|---|
| `Push(string key, double value, DateTimeOffset ts)` | Auto-creates channel via `GetOrAdd` |
| `GetChannel(string key) → MeasurementChannel?` | Lookup by key |
| `GetOrCreateChannel(string key) → MeasurementChannel` | Always returns channel |
| `GetChannelsForDevice(string deviceName) → IReadOnlyList` | Prefix match (`"deviceName."`) |
| `GetAllChannelKeys() → IReadOnlyList<string>` | All registered keys |
| `Clear()` | Clear all channels and data |

**Channel key convention:** `"{DeviceName}.CH{N}"` (e.g., `"Keithley-1.CH1"`, `"Keithley-1.CH2"`)

---

## 4. Process Execution Layer

**Project:** `Horizon.Process` (13 source files)

### Process Step Hierarchy

```csharp
ProcessStep (abstract)
  +-> ActionStep       { DeviceName, MethodName, Parameters }
  +-> LoopStep         { Count, Steps[] }
  +-> ConditionalStep  { ContextKey, Comparison, Threshold, ThenSteps[], ElseSteps[] }
  +-> StepMarkerStep   { StepIndex, StepName, StepType }
```

### ProcessEngine

| Method | Description |
|---|---|
| `BuildDeviceRegistry(object context)` | Auto-register devices from AppContext via reflection |
| `RegisterDevice(string name, object? mgr, string type)` | Manual device registration |
| `ExecuteAsync(steps, context, ct) → Task<bool>` | Execute process steps |
| `ContinueStep()` | Resume from breakpoint/step-by-step pause |

**Events:** `OnStepStarted(index, device, method)`, `OnStepCompleted(index, success)`

### Device Registry (built via reflection)

| Source Manager | Type String | Devices |
|---|---|---|
| UnifiedMotionManager | `"motion"` | All motion controller device names |
| EziIOManager | `"io"` | All IO device names |
| PneumaticManager | `"pneumatic"` | All pneumatic slide names |
| CameraManager | `"camera"` | All camera IDs |
| CurrentMeasurementManager | `"currentmeasurement"` | All CMeas device names |
| (virtual) | `"system"` | `"system"` — Wait, Log |
| (virtual) | `"context"` | `"context"` — SetValue, Calculate |

### CommandDispatcher

Routes commands to 7 registered `ICommandHandler` implementations:

| Handler | Type | Key Commands |
|---|---|---|
| MotionCommandHandler | `motion` | MoveToNamedPosition, MoveToAbs, MoveRelative, HomeAxis, StopAxis, GetPosition |
| IOCommandHandler | `io` | SetOutput, SetOutputByName, ClearOutputByName, ReadInput |
| PneumaticCommandHandler | `pneumatic` | Extend, Retract, ExtendAndWait, RetractAndWait, GetStatus |
| CameraCommandHandler | `camera` | CaptureToFile, StartGrabbing, StopGrabbing, Connect, Disconnect |
| CurrentMeasurementCommandHandler | `currentmeasurement` | Read_Current, Read_All |
| SystemCommandHandler | `system` | Wait, Log (with context variable substitution) |
| ContextCommandHandler | `context` | SetValue, GetValue, Calculate, IncrementCounter |

### ProcessContext (Runtime Variable Store)

Thread-safe `ConcurrentDictionary<string, string>` for sharing data between steps:

```csharp
context.SetValue("measured_value", "1.234");
context.GetNumeric("measured_value", 0.0);    // returns 1.234

// Motion handler stores: "{deviceName}.X", "{deviceName}.Y", "{deviceName}.Z"
// CMeas handler stores: "{deviceName}.current.ch1", "{deviceName}.current.ch2"
// Pneumatic handler stores: "{deviceName}.position"
```

---

## 5. Manager Layer

### 5.1 CameraManager

**File:** `Horizon.Camera/CameraManager.cs`

Manages camera lifecycle and frame broadcasting (pub/sub pattern):

- `AddCamera()`, `RemoveCamera()`, `ConnectCamera()`, `DisconnectCamera()`
- `StartGrabbingWithBroadcast()` — starts frame capture + subscriber notification
- `SubscribeToFrames()` / `UnsubscribeFromFrames()` — observer pattern
- `DiscoverPylonCameras()`, `DiscoverIDSCameras()` — static discovery methods
- `CameraConfigStore` — JSON persistence for registered cameras (`%APPDATA%\Horizon\camera-config.json`)

### 5.2 UnifiedMotionManager

**File:** `Horizon.Motion/UnifiedMotionManager.cs`

Multiplexes ACS and PI motion controllers:

```
UnifiedMotionManager
  +-> ACSControllerManager
  |     +-> ACSMotionController(s) — TCP port 701
  +-> PIControllerManager
        +-> PIMotionController(s) — TCP port 50000
```

Key operations: `MoveToAbs()`, `MoveToAbs6()` (6-axis hexapod), `MoveToNamedPosition()`, `MoveRelative()`, `HomeAxis()`, `StopAxis()`, `GetCurrentPosition()`

Named positions loaded from `motor_devices.json` with axis values (X,Y,Z,U,V,W).

### 5.3 EziIOManager

**File:** `Horizon.IO/EziIOManager.cs`

Fastech Ezi-IO digital I/O with named pin mapping:

- `ReadInputs()`, `SetOutput()`, `SetOutputByName()`
- Standalone IO modules use combined register: bits `[0..InputCount-1]` = inputs, bits `[InputCount..InputCount+OutputCount-1]` = outputs
- Motor IO (`FAS_SetIOOutput`) does not use this offset
- Polling via `PeriodicTimer`, `OnIOStatusUpdated` event

### 5.4 PneumaticManager

**File:** `Horizon.Pneumatic/PneumaticManager.cs`

Pneumatic slides controlled via IO output pins with sensor feedback:

- `Extend()`, `Retract()`, `ExtendAndWait()`, `RetractAndWait()`
- Each slide maps to: output pin, extended sensor pin, retracted sensor pin
- Position enum: Extended, Retracted, Extending, Retracting, Unknown, HardwareError, Timeout

### 5.5 CurrentMeasurementManager

**File:** `Horizon.CurrentMeasurement/CurrentMeasurementManager.cs`

Keithley picoammeter management:

- `ConnectAll()`, `DisconnectAll()`, `ReadCurrent()`, `ReadAllChannels()`
- `ConfigureFastMode()` / `ConfigureDefaultMode()` — NPLC switching for fast polling
- `SetVoltage()`, `SetVoltageEnabled()` — V-source control
- `GetIdentity()` — SCPI `*IDN?` query

### 5.6 SafetyWatchManager

**File:** `Horizon.Safety/SafetyWatchManager.cs`

I/O edge detection for safety monitoring:

- `LoadConfig()`, `StartWatching()`, `StopWatching()`
- `SafetyTriggered` / `SafetyCleared` events
- Configurable trigger edge (Rising, Falling, Both) per watched input
- `SafetyEventLog` — JSON audit trail with severity levels

---

## 6. Hardware Abstraction Interfaces

### Motion

```csharp
interface IMotionController {
    bool Connect(); bool Disconnect(); bool IsConnected { get; }
    MotionError MoveToPosition(string axis, double position);
    MotionError MoveRelative(string axis, double distance);
    bool IsMoving(); bool IsOnTarget();
    double[] GetCurrentPosition();
    MotionError HomeAxis(string axis);
    MotionError StopAxis(string axis);
    string ControllerType { get; } string DeviceName { get; }
    IReadOnlyList<string> AxisNames { get; }
}
```

### Camera

```csharp
interface ICameraHardware {
    bool Initialize(); bool Connect(); bool Disconnect();
    bool StartGrabbing(); bool StopGrabbing();
    bool CaptureFrame(out CameraFrameData frame);
    bool IsConnected { get; } bool IsGrabbing { get; }
    string CameraId { get; } string ModelName { get; } string SerialNumber { get; }
    CameraType CameraType { get; }
    ExposureSettings ExposureSettings { get; set; }
    Action<CameraFrameData>? FrameCallback { set; }
}
```

### I/O

```csharp
interface IIODevice {
    bool Connect(); bool Disconnect(); bool IsConnected { get; }
    bool ReadInputs(out ulong inputState);
    bool SetOutput(int pin, bool state);
    bool SetOutputByName(string pinName, bool state);
    ulong ReadAllOutputs();
    int InputCount { get; } int OutputCount { get; }
    string DeviceName { get; }
}
```

### Pneumatic

```csharp
interface IPneumaticSlide {
    PneumaticError Extend(); PneumaticError Retract();
    PneumaticError ExtendAndWait(int timeoutMs);
    PneumaticError RetractAndWait(int timeoutMs);
    PneumaticStatus GetStatus(); PneumaticPosition GetPosition();
    string SlideName { get; }
}
```

### Current Measurement

```csharp
interface ICurrentMeasurementDevice {
    bool Connect(); bool Disconnect(); bool IsConnected { get; }
    CurrentMeasurementError ReadCurrent(int channel, out double current);
    CurrentMeasurementError ReadAllChannels(out double[] currents);
    CurrentMeasurementError GetIdentity(out string identity);
    CurrentMeasurementError Reset();
    CurrentMeasurementError SetVoltage(double volts);
    CurrentMeasurementError SetVoltageEnabled(bool enabled);
    CurrentMeasurementError ConfigureFastMode();
    CurrentMeasurementError ConfigureDefaultMode();
    int ChannelCount { get; } IReadOnlyList<int> EnabledChannels { get; }
}
```

---

## 7. Adapter / Device Layer

### Motion Controllers

```
IMotionController
  +-> ACSMotionController
  |     +-> ACS.SPiiPlusNET6.dll (managed)
  |     +-> TCP/IP to ACS SPiiPlus controller (port 701)
  |     +-> 3-axis gantry (X, Y, Z)
  |
  +-> PIMotionController
        +-> PI_GCS2_DLL_x64.dll (P/Invoke via PIGcs2.cs)
        +-> TCP/IP to PI hexapod controller (port 50000)
        +-> 6-axis (X, Y, Z, U, V, W)
```

### Camera Adapters

```
ICameraHardware
  +-> PylonCameraAdapter
  |     +-> Basler.Pylon.dll (managed)
  |     +-> GigE Vision / USB3 cameras
  |     +-> Format converter: raw -> RGB8
  |
  +-> IDSPeakCameraAdapter
  |     +-> ids_peak_dotnet.dll + ids_peak_ipl_dotnet.dll (managed)
  |     +-> IDS industrial cameras
  |
  +-> UEyeCameraAdapter
        +-> uEye legacy SDK (P/Invoke via UEyeNative.cs)
        +-> Older IDS cameras
```

### I/O Adapters

```
IIODevice
  +-> EziIODevice
        +-> EziMOTIONPlusE.dll (P/Invoke via EziNative.cs)
        +-> TCP/IP to Fastech Ezi-IO modules
        +-> Combined I/O register with automatic bit offset
```

### Current Measurement Adapters

```
ICurrentMeasurementDevice
  +-> Keithley6482Adapter
        +-> NI-VISA (P/Invoke via VisaNative.cs)
        +-> GPIB/USB to Keithley 6482 picoammeter
        +-> 2-channel, pA to mA range
        +-> NPLC-based fast/default modes
        +-> V-source control
```

---

## 8. SDK / Protocol Layer

| SDK | Protocol | Connection | Used By |
|-----|----------|------------|---------|
| Basler Pylon 8 | GigE Vision | IP / USB3 | PylonCameraAdapter |
| IDS Peak SDK | GenICam | USB3 / GigE | IDSPeakCameraAdapter |
| ACS.SPiiPlusNET6 | Proprietary | TCP port 701 | ACSMotionController |
| PI GCS2 DLL (x64) | GCS2 | TCP port 50000 | PIMotionController |
| Fastech EziMOTION | Proprietary | TCP | EziIODevice |
| NI-VISA | VISA/SCPI | GPIB / USB | Keithley6482Adapter |

### Native DLL Deployment

| DLL | Source Path | Deployment |
|-----|------------|------------|
| `PI_GCS2_DLL_x64.dll` | `C:\Program Files (x86)\Physik Instrumente (PI)\...` | `CopyToOutputDirectory="PreserveNewest"` |
| `EziMOTIONPlusE.dll` | `C:\Program Files\FASTECH\Ezi-MOTION Plus-E V6\...` | `CopyToOutputDirectory="PreserveNewest"` |
| VISA runtime | NI-VISA installer | System-wide (GAC) |

---

## 9. Safety Layer (Cross-Cutting)

```
SafetyWatchManager (PeriodicTimer polling)
  |
  +-> Reads safety I/O pins via EziIOManager
  +-> Edge detection (Rising, Falling, Both) per watched input
  +-> On trigger: fires SafetyTriggered event
  |     +-> IsCritical flag per input
  |     +-> AlertTitle + AlertMessage per input
  |
  +-> SafetyEventLog
        +-> JSON audit trail with timestamps
        +-> Severity: Info, Warning, Critical, Emergency
```

---

## 10. Configuration System

All hardware config is JSON-based in `%APPDATA%\Horizon\config\`:

| File | Purpose |
|------|---------|
| `motor_devices.json` | Motion controllers, axes, named positions |
| `io_devices.json` | Ezi-IO device addresses and pin name mappings |
| `pneumatic_slides.json` | Slide configs, sensor pins, timeouts |
| `safety_watch.json` | Safety I/O pins, trigger edges, severity |
| `current_measurement_devices.json` | VISA resources, enabled channels |

**Camera config:** `%APPDATA%\Horizon\camera-config.json` (separate, managed by CameraConfigStore)

**Local override pattern:** `xxx.local.json` takes precedence over `xxx.json` — for machine-specific settings.

**JSON options:** `System.Text.Json` with `PropertyNameCaseInsensitive = true`, `ReadCommentHandling = JsonCommentHandling.Skip`, `AllowTrailingCommas = true`.

---

## 11. Project Dependency Graph

```
Horizon.App (net8.0-windows, WPF)
  +-> Horizon.Core
  |     +-> Horizon.Camera ---------> Horizon.Common
  |     +-> Horizon.IO --------------> Horizon.Common
  |     +-> Horizon.Motion ----------> Horizon.Common
  |     +-> Horizon.Pneumatic -------> Horizon.Common + Horizon.IO
  |     +-> Horizon.CurrentMeasurement -> Horizon.Common
  |     +-> Horizon.Safety ----------> Horizon.Common + Horizon.IO
  |     +-> Horizon.Process ---------> Horizon.Common
  |     +-> Horizon.Common (leaf)
  +-> Horizon.IO (direct, for UI panels)
  +-> Horizon.Motion (direct, for UI panels)
  +-> Horizon.Pneumatic (direct, for UI panels)
  +-> Horizon.CurrentMeasurement (direct, for UI panels)
```

**NuGet Packages:**
- `Dirkster.AvalonDock` 4.72.1 (Horizon.App)
- `Dirkster.AvalonDock.Themes.VS2013` 4.72.1 (Horizon.App)
- `SkiaSharp.Views.WPF` 3.116.1 (Horizon.App — for OverlayWindow)

---

## 12. Key Design Patterns

| Pattern | Where Used |
|---------|------------|
| **Dependency Injection** | AppContext owns + exposes all managers via IAppContext |
| **Manager** | Each hardware subsystem has a dedicated manager class |
| **Factory** | CameraHardwareFactory selects adapter by CameraType |
| **Strategy** | IMotionControllerManager allows ACS/PI interchangeability |
| **Adapter** | PylonCameraAdapter, PIMotionController wrap vendor SDKs |
| **Command/Dispatcher** | ProcessEngine -> CommandDispatcher -> ICommandHandler |
| **Builder** | ProcessBuilder for constructing step sequences |
| **Observer/Pub-Sub** | Camera frame broadcasting, MeasurementHub push/read |
| **Circular Buffer** | MeasurementChannel for time-series data (lock-based) |
| **Config Override** | `.local.json` overlay pattern for machine-specific settings |

---

## 13. Error Handling

Each domain has a typed error enum:

| Enum | Hex Range | Domain |
|------|-----------|--------|
| `IOError` | — | Digital I/O |
| `MotionError` | — | Motion controllers |
| `PneumaticError` | — | Pneumatic slides |
| `CurrentMeasurementError` | — | Picoammeter |
| `ErrorCode` (unified) | `0x0000`–`0x0700` | Cross-domain (Common) |

`ErrorCategory` enum groups ErrorCode values: General, Motion, IO, Pneumatic, Camera, CurrentMeasurement, Process, Safety.

`Result<T>` struct provides generic success/failure container with ErrorCode + Message.

---

## 14. Complete Data Flow Example

**CurrentMeasurementPanel fast-polling to chart overlay on camera view:**

```
1. User double-clicks "Keithley-1" in hardware tree
   -> MainWindow creates CurrentMeasurementPanel(deviceName, manager, hub)

2. Panel builds channel keys: ["Keithley-1.CH1", "Keithley-1.CH2"]
   -> ReadingChart.Attach(hub, channelKeys)
   -> ReadingChart.StartRendering() (33ms DispatcherTimer)

3. User clicks "Start Poll" -> ConfigureFastMode() -> FastPollLoopAsync()

4. FastPollLoopAsync (background thread, tight loop):
   device.ReadAllChannels(out readings)  // VISA SCPI ":READ?" command
   hub.Push("Keithley-1.CH1", readings[0], now)  // auto-creates channel
   hub.Push("Keithley-1.CH2", readings[1], now)
   Dispatcher.Invoke() -> update TextBlock readings + Hz rate

5. CurrentReadingChart.RenderFrame (UI thread, 30fps):
   channel1 = hub.GetChannel("Keithley-1.CH1")
   count1 = channel1.Snapshot(timestamps1, values1)
   channel2 = hub.GetChannel("Keithley-1.CH2")
   count2 = channel2.Snapshot(timestamps2, values2)
   -> find global tMax, tMin, yMin, yMax
   -> map to WPF Polyline points
   -> update grid lines + SI-formatted axis labels

6. OverlayWindow (separate window, SkiaSharp rendering):
   MeasCombo selects device -> builds channelKeys
   DrawChartOverlay():
     snapshot each channel independently
     same auto-scale logic, but rendered via SKPath on SKCanvas
     semi-transparent panel overlaid on camera frame

7. User switches MeasCombo to "PowerMeter-1" (future)
   -> new channelKeys built -> chart switches seamlessly
```
