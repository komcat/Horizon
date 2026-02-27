# Bobcat Engine - Architecture Layers

This document describes the application's layered architecture from top-level initialization down to low-level hardware controllers.

---

## Layer Diagram (Overview)

```
+-----------------------------------------------------------------------+
|                        Application (Entry Point)                       |
|   Lifecycle: initialize() -> initializeManagers() -> createMainWindow()|
|              -> connectHardwareAsync() -> exec() -> shutdown()         |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                      ApplicationContext (DI Container)                  |
|   Raw pointer struct holding all 23+ managers. Ownership in Application|
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                    Recipe / Program Execution Layer                     |
|   ProgramManager -> RecipeWorker -> ProcessPluginRegistry              |
|   RecipeExecutionStateMachine (DAG-based multi-recipe orchestration)   |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                    Sequence Execution Layer                             |
|   SequenceEngine -> CommandDispatcher -> ICommandHandler (18 handlers) |
|   SequenceBuilder | SequenceContext | Timeout/Safety enforcement       |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                        Manager Layer                                   |
|   UnifiedMotionManager | CameraManager | EziIOManager | TECManager    |
|   PowerSupplyManager | SMUManager | LightingManager | PneumaticManager|
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                   Hardware Abstraction Interfaces                       |
|   IMotionControllerManager | ICameraHardware | IEziIODevice           |
|   ITECDevice | IPowerSupply | ISMUDevice | ILightingController        |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                      Adapter / Device Layer                             |
|   ACSController | PIController | PylonCameraAdapter | IDSCameraAdapter|
|   WagoIODevice | CLD101xAdapter | TED4015DirectAdapter | etc.        |
+-----------------------------------------------------------------------+
         |
         v
+-----------------------------------------------------------------------+
|                        SDK / Protocol Layer                             |
|   ACSC SDK | PI GCS2 DLL | Pylon SDK | IDS uEye SDK | NI-VISA       |
|   Modbus TCP | SCPI/TCP | Custom TCP protocols                       |
+-----------------------------------------------------------------------+
```

---

## 1. Application Layer

**Files:** `Application.h`, `Application.cpp`

The `Application` class owns all resources as `std::unique_ptr` members and orchestrates the full lifecycle:

### Initialization Sequence

```
Application::run()
  |
  +-> initialize()
  |     +-> Create QApplication (Fusion style, dark theme, RobotoMono font)
  |     +-> Check BOBCAT_SIMULATION env var
  |     +-> Show ConnectionSplashScreen (hardware mode only)
  |     +-> initializeManagers()        -- sequential, 20+ managers
  |     +-> createMainWindow()          -- UI shown before hardware connects
  |     +-> QTimer::singleShot(100ms)   -- defer connectHardwareAsync()
  |
  +-> m_qApplication->exec()            -- Qt event loop starts
  |     |
  |     +-> [100ms later] connectHardwareAsync()
  |           +-> ConnectionWorkerPool executes parallel connection tasks
  |           +-> Phase 0: TCP + Cameras
  |           +-> Phase 1: Motion + I/O
  |           +-> Phase 2: TEC + Lighting
  |
  +-> shutdown()                         -- destroy managers in reverse order
```

### Manager Initialization Order

Managers are created sequentially in strict dependency order:

1. LogManager
2. ApplicationSettings
3. DataStore
4. TcpClientManager
5. CameraManager
6. UnifiedMotionManager
7. EziIOManager
8. PneumaticManager
9. TECManager
10. PowerSupplyManager
11. SMUManager
12. AuxWindowManager
13. GdiCameraWindowManager (x2)
14. ProductPositionManager
15. DeviceLinkManager *(must precede SequenceEngine)*
16. SequenceEngine
17. AdaptiveScanController
18. CoordinateSystemManager
19. LightingManager
20. BeamServerClient
21. SafetyEventLog *(must precede SafetyWatchManager)*
22. SafetyWatchManager

### Hardware Connection (Deferred)

`ConnectionWorkerPool` runs connection tasks in background threads organized into phases. Within each phase, tasks execute in parallel. Phases execute sequentially (barrier pattern).

```
ConnectionWorkerPool
  +-> Phase 0 (parallel): TCP clients, Cameras
  +-> Phase 1 (parallel): Motion controllers, I/O devices
  +-> Phase 2 (parallel): TEC devices, Lighting controllers
```

Each task has a configurable timeout and emits Qt signals for splash screen progress updates.

---

## 2. ApplicationContext (Dependency Injection)

**File:** `include/context/ApplicationContext.h`

A plain struct holding raw pointers to all managers:

```cpp
struct ApplicationContext {
    CameraManager*            cameraManager;
    LogManager*               logManager;
    DataStore*                dataStore;
    TcpClientManager*         tcpClientManager;
    UnifiedMotionManager*     motionManager;
    EziIOManager*             ioManager;
    PneumaticManager*         pneumaticManager;
    SequenceEngine*           sequenceEngine;
    ProductPositionManager*   productPositionManager;
    AdaptiveScanController*   scanController;
    TECManager*               tecManager;
    AuxWindowManager*         auxWindowManager;
    PowerSupplyManager*       powerSupplyManager;
    SMUManager*               smuManager;
    SMUPollingManager*        smuPollingManager;
    CoordinateSystemManager*  coordinateSystemManager;
    DeviceUnderTest*          currentDUT;
    Lighting::LightingManager* lightingManager;
    SafetyWatchManager*       safetyWatchManager;
    DeviceLinkManager*        deviceLinkManager;
    GdiCameraWindowManager*   gdiCameraWindowManager;
    GdiCameraWindowManager*   gdiCameraWindowManager2;
    ApplicationSettings*      applicationSettings;
    SafetyEventLog*           safetyEventLog;
    BeamServerClient*         beamServerClient;
    bool                      simulationMode;
};
```

**Ownership model:** `Application` owns all managers via `std::unique_ptr`. ApplicationContext holds **borrowed** raw pointers. All managers receive `&m_context` to access sibling managers.

---

## 3. Recipe / Program Execution Layer

### Recipe Data Model

**Files:** `include/recipe/RecipeManager.h`, `include/recipe/IProcessPlugin.h`, `include/recipe/ProcessPluginRegistry.h`

```
Recipe (JSON file)
  +-> steps[]: ProcessInstance
        +-> processType: string     ("Pick", "Place", "Wait", ...)
        +-> userNickname: string    ("Pick Left Lens")
        +-> enabled: bool
        +-> parameters: map<string, string>
```

### Plugin System

Each process type is an `IProcessPlugin` implementation:

```cpp
class IProcessPlugin {
    virtual string GetName() const = 0;
    virtual string GetCategory() const = 0;
    virtual map<string, string> GetDefaultParameters() const = 0;
    virtual void CreateConfigUI(ProcessConfigWidget*, const ProcessInstance*) = 0;
    virtual void BuildSequence(SequenceBuilder*, const ProcessInstance*) = 0;
};
```

Plugins self-register at static initialization via `ProcessPluginRegistry`:

```cpp
namespace {
  struct RegisterMotionProcesses {
    RegisterMotionProcesses() {
      auto& reg = ProcessPluginRegistry::Instance();
      reg.RegisterPlugin(std::make_unique<PickPlugin>());
      reg.RegisterPlugin(std::make_unique<PlacePlugin>());
      // ...
    }
  };
  static RegisterMotionProcesses g_register;
}
```

**25 plugin files** organized by domain: Motion, System, IO, Camera, TEC, PSU, SMU, Vision, DataStore, DUT, Conditional, Calibration, Lighting, AuxWindow, Dispense, etc.

### Recipe Execution Pipeline

```
Recipe JSON  --->  RecipeManager::loadFromJson()
                         |
                         v
                   ProcessInstance[]
                         |
             +-----------+-----------+
             |                       |
     Single-Recipe Path      Multi-Recipe Path
             |                       |
      RecipeWorker             ProgramManager
      (QThread)                      |
             |               RecipeExecutionStateMachine
             |               (DAG with dependencies)
             v                       |
  buildSequenceFromRecipe()          v
             |               For each ready recipe:
             v                 spawn RecipeWorker
  For each enabled step:
    ProcessPluginRegistry::GetPlugin(processType)
    plugin->BuildSequence(sequenceBuilder, step)
             |
             v
  SequenceEngine::ExecuteSequence(sequence, context)
```

### RecipeExecutionStateMachine

Manages multi-recipe orchestration with DAG-based dependencies:

- States: Pending -> Ready -> Running -> Completed/Failed/Skipped
- Topological sort for execution order
- Cycle detection and prerequisite validation
- Conditional execution based on SequenceContext values

---

## 4. Sequence Execution Layer

**Files:** `include/sequence/SequenceEngine.h/.cpp`, `include/sequence/CommandDispatcher.h/.cpp`, `include/sequence/SequenceBuilder.h`

### Sequence Step Hierarchy

```cpp
ISequenceStep (abstract)
  +-> Action          { deviceName, methodName, params }
  +-> LoopBlock       { count, actions[] }
  +-> ConditionalBlock { contextKey, comparison, threshold, thenActions[], elseActions[] }
  +-> StepMarker      { stepIndex, stepName, stepType }
```

### SequenceBuilder

Plugins use this fluent API to construct sequences:

```cpp
sequence->AddAction("hex-left", "MoveToNamedPosition", {{"position", "lensgrip"}});
sequence->AddAction("IOBottom", "SetOutput", {{"pin", "0"}, {"state", "1"}});
sequence->AddAction("system", "Wait", {{"milliseconds", "500"}});

sequence->BeginLoop(3);
  sequence->AddAction("cam-main", "CaptureToFile", {{"filename", "test"}});
sequence->EndLoop();

sequence->BeginIf("measured_value", GREATER_THAN, 1.5f);
  sequence->AddAction("system", "Log", {{"message", "PASS"}});
sequence->Else();
  sequence->AddAction("system", "Log", {{"message", "FAIL"}});
sequence->EndIf();
```

### SequenceEngine Execution Loop

```
ExecuteSequence(sequence, context)
  |
  +-> Validate sequence (device registry checks)
  +-> Log to SafetyEventLog: SEQUENCE_START
  +-> BuildActionList() for look-ahead
  |
  +-> For each step:
        +-> Check m_stopRequested (atomic flag)
        +-> ExecuteStep(step, context)
              |
              +-> ACTION:
              |     +-> ResolveManager(deviceName)
              |     +-> ResolveManagerType(deviceName)
              |     +-> IsDeviceConnected() pre-check
              |     +-> InjectContinuousVisionResult() -> context
              |     +-> InjectBeamStreamResult() -> context
              |     +-> DispatchWithTimeout()
              |           +-> Spawn std::thread
              |           +-> CommandDispatcher::ExecuteCommand()
              |           +-> cv.wait_for(timeout)
              |           +-> On timeout: zombie thread + CRITICAL log
              |
              +-> LOOP: Iterate, recurse into inner steps
              +-> CONDITIONAL: Evaluate context, execute branch
              +-> STEP_MARKER: Handle breakpoint/pause, fire callbacks
```

### Device Registry

`BuildDeviceRegistry()` maps device names to (manager*, managerType):

| Device Name | Manager Type | Manager |
|-------------|-------------|---------|
| `"gantry-main"` | `"motion"` | motionManager |
| `"hex-left"` | `"motion"` | motionManager |
| `"IOBottom"` | `"io"` | ioManager |
| `"slide-1"` | `"pneumatic"` | pneumaticManager |
| `"cam-main"` | `"camera"` | cameraManager |
| `"tec-main"` | `"tec"` | tecManager |
| `"psu-1"` | `"psu"` | powerSupplyManager |
| `"smu-keithley"` | `"smu"` | smuManager |
| `"light-main"` | `"lighting"` | lightingManager |
| `"system"` | `"system"` | nullptr (virtual) |
| `"context"` | `"context"` | nullptr (virtual) |
| `"vision"` | `"vision"` | nullptr (virtual) |
| `"dut"` | `"dut"` | nullptr (virtual) |
| `"datastore"` | `"datastore"` | dataStore |
| `"calibration"` | `"calibration"` | nullptr (virtual) |
| `"parallel"` | `"motion"` | motionManager (special) |

### CommandDispatcher

Routes commands to the appropriate handler based on manager type:

```
CommandDispatcher
  +-> m_handlers: map<string, unique_ptr<ICommandHandler>>
        "motion"          -> MotionCommandHandler
        "io"              -> IOCommandHandler
        "pneumatic"       -> PneumaticCommandHandler
        "camera"          -> CameraCommandHandler
        "tec"             -> TECCommandHandler
        "psu"             -> PSUCommandHandler
        "smu"             -> SMUCommandHandler
        "lighting"        -> LightingCommandHandler
        "system"          -> SystemCommandHandler
        "context"         -> SequenceContextCommandHandler
        "vision"          -> VisionCommandHandler
        "dut"             -> DUTCommandHandler
        "datastore"       -> DataStoreCommandHandler
        "calibration"     -> CalibrationCommandHandler
        "auxwindow"       -> AuxWindowCommandHandler
        "relpos"          -> RelativePositionCommandHandler
        "coordalign"      -> CoordinateAlignCommandHandler
        "accuracytrainer" -> AccuracyTrainerCommandHandler
```

### SequenceContext (Runtime Variable Store)

Commands share data via key-value context:

```cpp
context->Set("measured_value", "1.234");
context->GetNumeric("measured_value", 0.0);    // returns 1.234

// Vision injection: cv.* keys (circle center, line endpoints, etc.)
// Beam stream injection: bs.* keys (centroids, d4sigma, intensity)
// Referenced in params as "context:variable_name"
```

### Safety Enforcement

- **Pre-dispatch check:** `IsDeviceConnected()` before every action
- **Timeout enforcement:** `DispatchWithTimeout()` with configurable limit (default 60s)
- **Post-dispatch TOCTOU check:** Re-verify connectivity after failures
- **Zombie thread management:** Timed-out threads stored and joined at cleanup
- **Stop/Pause control:** Atomic flags, condition_variable, step-by-step mode
- **Breakpoints:** Per-step breakpoints with callback notification

---

## 5. Manager Layer

Each hardware domain has a dedicated manager. Managers are created and owned by `Application`, wired into `ApplicationContext`, and accessed by command handlers.

### 5.1 UnifiedMotionManager

**Files:** `include/motions/unified_motion_manager.h/.cpp`

Routes commands to the correct controller brand based on device type:

```
UnifiedMotionManager
  +-> m_managers: map<string, unique_ptr<IMotionControllerManager>>
  |     +-> "ACS" -> ACSControllerManager
  |     +-> "PI"  -> PIControllerManager
  |
  +-> m_deviceTypes: map<deviceName, controllerType>
  +-> m_namedPositions: map<device, map<posName, PositionStruct>>
  +-> m_waypointStates: map<device, DeviceWaypointState>
```

**Key operations:**
- `MoveToAbs(device, x, y, z)` - routes to ACS or PI controller
- `MoveToAbs6(device, x, y, z, u, v, w)` - 6-axis with axis filtering
- `MoveToNamedPosition(device, name)` - with waypoint state machine
- `GetPosition()`, `IsMoving()`, `HomeAxis()`, `StopAxis()`

**Waypoint Navigation State Machine:**

```
MoveToNamedPosition("hex-left", "lensgrip")
  |
  +-> Check exit waypoints from current position
  +-> Check entry waypoints on target position
  |
  State: Idle
    -> MovingThroughExitWaypoints (queue exit waypoints)
    -> MovingThroughEntryWaypoints (queue entry waypoints)
    -> MovingToFinalTarget (direct move)
    -> Idle (update currentPositionName)
```

**Calculated Positions:** Offset notation `"safe+Y1+Z-2"` parsed at runtime:
- Base position looked up from named positions
- Axis offsets applied additively
- Waypoints cleared (calculated positions don't inherit waypoints)

### 5.2 CameraManager

**Files:** `include/camera/CameraManager.h/.cpp`

Manages camera lifecycle and frame broadcasting:

```
CameraManager
  +-> m_cameras: map<string, ManagedCamera>
  |     +-> info: ExtendedCameraInfo
  |     +-> camera: unique_ptr<ICameraHardware>
  |
  +-> m_subscribers: map<string, SubscriberInfo>
  +-> m_broadcastThread: std::thread
  +-> m_globalBroadcastRate: float (FPS)
```

**Broadcasting (Pub/Sub):**
1. Camera adapter acquires frame -> calls frameCallback
2. CameraManager::BroadcastFrame() iterates subscribers
3. Rate-limits per subscriber's `GetMinFrameIntervalMs()`
4. Calls `subscriber.OnNewFrame(frameData)` on broadcast thread

### 5.3 Other Managers

| Manager | Domain | Key Operations |
|---------|--------|---------------|
| `EziIOManager` | Digital I/O | Read/write pins, polling, latch detection |
| `PneumaticManager` | Pneumatic slides | Extend, retract, wait, state machine |
| `TECManager` | Temperature control | Set temperature, measure, enable output |
| `PowerSupplyManager` | Power supplies | Set voltage/current, OVP/OCP, enable |
| `SMUManager` | Source measure | Configure source/measure, compliance |
| `SMUPollingManager` | SMU polling | Background measurement worker threads |
| `LightingManager` | LED controllers | Set intensity, channel on/off |
| `SafetyWatchManager` | Safety monitoring | I/O polling, E-stop broadcast |
| `VisionManager` | Image processing | Plugin registry, execute pipeline |

---

## 6. Hardware Abstraction Interfaces

Each subsystem defines a pure virtual interface that adapters implement:

### Motion

```cpp
// include/motions/interfaces/IMotionControllerManager.h
class IMotionControllerManager {
    virtual void AddDevice(const MotionDevice& device) = 0;
    virtual bool ConnectAll() = 0;
    virtual void DisconnectAll() = 0;
    virtual bool ConnectDevice(const string& deviceName) = 0;
    virtual vector<string> GetDeviceNames() const = 0;
    virtual string GetControllerType() const = 0;
    virtual void* GetController(const string& deviceName) = 0;
};
```

### Camera

```cpp
// include/camera/ICameraHardware.h
class ICameraHardware {
    virtual bool Initialize() = 0;
    virtual bool Connect() = 0;
    virtual bool Disconnect() = 0;
    virtual bool StartGrabbing() = 0;
    virtual bool StopGrabbing() = 0;
    virtual bool CaptureFrame(CameraFrameData& frameData) = 0;
    virtual bool SetExposureSettings(const ExposureSettings& settings) = 0;
    virtual void SetFrameCallback(function<void(const CameraFrameData&)> cb) = 0;
    virtual CameraType GetCameraType() const = 0;
};
```

### I/O

```cpp
// include/IO/IEziIODevice.h
class IEziIODevice {
    virtual bool Connect() = 0;
    virtual bool IsConnected() const = 0;
    virtual bool ReadInput(int pin) = 0;
    virtual bool SetOutput(int pin, bool state) = 0;
    virtual uint64_t ReadAllInputs() = 0;
    virtual uint64_t ReadAllOutputs() = 0;
};
```

### TEC, PSU, SMU, Lighting

```cpp
// Similar pattern for each:
class ITECDevice { /* SetTemperature, GetTemperature, EnableOutput, ... */ };
class IPowerSupply { /* SetVoltage, SetCurrent, EnableOutput, ... */ };
class ISMUDevice { /* ConfigureSource, Measure, EnableOutput, ... */ };
class ILightingController { /* SetIntensity, ChannelOn/Off, ... */ };
```

---

## 7. Adapter / Device Layer

Adapters implement the hardware abstraction interfaces for specific hardware:

### Motion Controllers

```
IMotionControllerManager
  +-> ACSControllerManager
  |     +-> ACSController (per device)
  |           +-> ACSC_wrapper (C SDK wrapper)
  |           +-> TCP/IP to ACS SPiiPLUS controller (port 701)
  |           +-> Features: 3-axis gantry, buffer programs, communication thread
  |
  +-> PIControllerManager
        +-> PIController (per device)
              +-> PI_GCS2_DLL (GCS2 protocol wrapper)
              +-> TCP/IP to PI hexapod controller (port 50000)
              +-> Features: 6-axis (XYZUVW), fast scan, analog input, pivot point
```

### Camera Adapters

```
ICameraHardware
  +-> PylonCameraAdapter
  |     +-> PylonCamera (legacy wrapper)
  |           +-> Pylon::CInstantCamera
  |           +-> Format converter: raw -> RGB8
  |           +-> Connection: IP, serial, index, auto
  |
  +-> IDSCameraAdapter
        +-> Direct uEye SDK calls (HCAM handle)
        +-> Grab thread: std::thread
        +-> Connection: device index
```

**Factory:** `CameraHardwareFactory::CreateCamera(type, info)` produces the right adapter.

### I/O Adapters

```
IEziIODevice
  +-> EziIODevice
  |     +-> Fastech EziIO SDK (PE::FAS_* API)
  |     +-> TCP/IP to EziIO controller
  |
  +-> WagoIODevice
        +-> Modbus TCP protocol (raw socket)
        +-> SocketGuard RAII wrapper
        +-> Coil/discrete input address mapping
```

### TEC Adapters

```
ITECDevice
  +-> CLD101xAdapter
  |     +-> TCP socket to Python server
  |     +-> SCPI commands (text protocol)
  |     +-> Controls TEC + laser current
  |     +-> SocketGuard RAII
  |
  +-> TED4015DirectAdapter
        +-> NI-VISA (USB/GPIB)
        +-> SCPI commands
        +-> TEC only (no laser)
```

### PSU Adapters

```
IPowerSupply
  +-> KeysightE36103B
  |     +-> Direct TCP/SCPI (port 5025)
  |     +-> SocketGuard RAII
  |
  +-> KeysightPSUAdapter
        +-> TCP/JSON to Python server proxy
        +-> SocketGuard RAII
```

### SMU Adapters

```
ISMUDevice
  +-> KeithleySMUAdapter
  |     +-> NI-VISA (GPIB/USB)
  |     +-> SCPI commands
  |     +-> Auto-detect 2400/2600 series
  |     +-> Single or dual channel
  |
  +-> Keithley6482Adapter
        +-> NI-VISA
        +-> Picoammeter (2nA to 21mA)
        +-> NPLC-based measurement modes
```

### Lighting Adapters

```
ILightingController
  +-> CCSPD3Adapter
        +-> TCP socket (port 40001)
        +-> Proprietary checksum protocol
        +-> Multi-channel intensity (0-255)
        +-> SocketGuard RAII
```

---

## 8. SDK / Protocol Layer

| SDK | Protocol | Connection | Used By |
|-----|----------|------------|---------|
| ACSC SDK | Proprietary | TCP port 701 | ACSController |
| PI GCS2 DLL | GCS2 | TCP port 50000 | PIController |
| Pylon 8 SDK | GigE Vision | IP/serial/index | PylonCameraAdapter |
| IDS uEye SDK | Proprietary | USB | IDSCameraAdapter |
| NI-VISA | VISA/SCPI | USB/GPIB | TED4015, Keithley SMUs |
| Fastech EziIO | Proprietary | TCP | EziIODevice |
| Modbus TCP | RFC 1006 | TCP | WagoIODevice |
| SCPI/TCP | SCPI text | TCP port 5025 | KeysightE36103B |
| Custom TCP/JSON | JSON-RPC-like | TCP | KeysightPSUAdapter, CLD101xAdapter |
| CCS PD3 | Checksum protocol | TCP port 40001 | CCSPD3Adapter |

---

## 9. Safety Layer (Cross-Cutting)

Safety spans all layers:

```
SafetyWatchManager (polling thread)
  |
  +-> Reads safety I/O pins via EziIOManager
  +-> On trigger: emits safetyTriggered signal
        |
        v
  Application::performEStopCascade()
        |
        +-> TECManager::DisableAllOutputs()
        +-> PowerSupplyManager::disableAllOutputs()
        +-> SMUManager::OutputOffAll()
        +-> PneumaticManager::StopAll()
        +-> LightingManager::AllOff()
        |
        +-> SafetyEventLog::LogEvent(CRITICAL, ESTOP_CASCADE_COMPLETE)
```

**Safety infrastructure:**
- `ErrorCode` enum (`include/common/ErrorCode.h`) - 40+ codes across 12 categories
- `SafetyEventLog` (`include/safety/SafetyEventLog.h`) - JSON-lines audit trail with rotation
- `ThreadPriority` (`include/common/ThreadPriority.h`) - AboveNormal for safety-critical threads
- `SocketGuard` (`include/IO/SocketGuard.h`) - RAII socket lifetime management

---

## 10. Complete Data Flow Example

**Recipe step "Pick Left Lens" executing:**

```
1. Recipe JSON loaded by RecipeManager
   {"type": "Pick", "parameters": {"gripper_device": "hex-left", ...}}

2. RecipeWorker iterates steps
   ProcessPluginRegistry::GetPlugin("Pick") -> PickPlugin

3. PickPlugin::BuildSequence()
   sequence->AddAction("hex-left", "MoveToNamedPosition", {{"position", "lensgrip"}});
   sequence->AddAction("IOBottom", "SetOutput", {{"pin", "0"}, {"state", "1"}});
   sequence->AddAction("system", "Wait", {{"milliseconds", "500"}});

4. SequenceEngine::ExecuteSequence()
   For each Action:

5. ExecuteStep(Action: "hex-left" / "MoveToNamedPosition")
   -> ResolveManager("hex-left") = motionManager
   -> ResolveManagerType("hex-left") = "motion"
   -> IsDeviceConnected("hex-left") pre-check
   -> DispatchWithTimeout("motion", motionManager, "hex-left", "MoveToNamedPosition", ...)

6. CommandDispatcher routes to MotionCommandHandler
   -> MotionCommandHandler::ExecuteCommand()
   -> Calls motionManager->MoveToNamedPosition("hex-left", "lensgrip", false)

7. UnifiedMotionManager routes by device type
   -> GetDeviceType("hex-left") = "PI"
   -> GetPIController("hex-left")
   -> Check waypoints: exit "safe" -> entry "safe" -> target "lensgrip"
   -> Init DeviceWaypointState, queue waypoints

8. PIController::MoveToPosition("X", x, false)  per axis
   -> PI GCS2 DLL sends motion commands over TCP/IP
   -> PI hexapod physically moves

9. MotionCommandHandler::WaitForMotionWithStopCheck()
   -> Monitors SequenceEngine stop flag
   -> Checks position tolerance (0.5mm)
   -> Returns when motion complete

10. Waypoint state machine advances through queue
    -> Moves through exit waypoints -> entry waypoints -> final target
    -> Updates currentPositionName to "lensgrip"

11. Next action executes...
```

---

## 11. Configuration System

All hardware config is JSON-based in `config/`:

| File | Purpose |
|------|---------|
| `motor_devices.json` | Motion controllers, axes, named positions, waypoints |
| `camera_config.json` | Camera type, connection, exposure settings |
| `io_devices.json` | Digital I/O device addresses and pin mappings |
| `pneumatic_slides.json` | Slide configs, sensor pins, timeouts |
| `tec_devices.json` | TEC device connections and types |
| `power_supply_config.json` | PSU channels and connection info |
| `smu_devices.json` | SMU device VISA addresses |
| `lighting_config.json` | Lighting controllers and channel names |
| `safety_watch.json` | Safety I/O pins and edge detection |
| `tcp_clients.json` | TCP client connections |

**Local override pattern:** `xxx.local.json` takes precedence over `xxx.json`. Local files are gitignored for machine-specific settings.

---

## 12. Key Design Patterns

| Pattern | Where Used |
|---------|------------|
| **Dependency Injection** | ApplicationContext struct passed to all managers |
| **Manager** | Each subsystem has a dedicated manager class |
| **Factory** | CameraHardwareFactory, hardware adapter creation |
| **Strategy** | IMotionControllerManager allows swapping ACS/PI implementations |
| **Adapter** | PylonCameraAdapter, IDSCameraAdapter wrap different SDKs |
| **Command/Dispatcher** | SequenceEngine -> CommandDispatcher -> ICommandHandler |
| **Builder** | SequenceBuilder for constructing command sequences |
| **Plugin** | IProcessPlugin for recipe processes, IVisionPlugin for vision |
| **State Machine** | Waypoint navigation, recipe execution |
| **Observer/Pub-Sub** | Camera frame broadcasting, Qt signals throughout |
| **RAII** | SocketGuard for sockets, unique_ptr for ownership |

---

## 13. Error Handling (No Exceptions)

MISRA-aligned error handling throughout:

- **ErrorCode enum** (`include/common/ErrorCode.h`) - 40+ codes (0x0000-0x0F00)
- **Return codes** from all hardware operations (bool success or ErrorCode)
- **`std::optional<T>`** for fallible constructors (e.g., `DeviceUnderTest::create()`)
- **`IsValid()` pattern** for objects that may fail initialization
- **`[[nodiscard]]`** on safety-critical return values
- **Bounded loops** (no `while(true)`) with maximum iteration counts
- **SocketGuard** RAII for all TCP socket lifetime management
