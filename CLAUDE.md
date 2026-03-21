# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

- **Solution**: `Horizon.sln` (11 projects, all net8.0, x64 only)
- **WPF app entry**: `Horizon.App` (net8.0-windows)
- **User compiles themselves** — do not run `dotnet build` or `msbuild`
- **User tests themselves** — no test projects exist; skip test commands
- No CI/CD pipelines configured

## Architecture

Modular hardware control system (WPF + AvalonDock) for cameras, motion stages, digital I/O, pneumatic slides, current measurement, laser controllers, process sequencing, and safety monitoring.

### Dependency Graph

```
Horizon.App (WPF shell)
  └─ Horizon.Core (AppContext wires all managers)
       ├─ Horizon.Process (CommandDispatcher + ProcessEngine + Plugins)
       │    ├─ Horizon.Camera
       │    ├─ Horizon.Motion
       │    ├─ Horizon.Pneumatic ─── Horizon.IO
       │    ├─ Horizon.IO
       │    ├─ Horizon.CurrentMeasurement
       │    ├─ Horizon.LaserController
       │    └─ Horizon.Safety ────── Horizon.IO
       └─ Horizon.Common (ErrorCode, Result<T>, ConfigLoader)
```

### AppContext (DI Container)

`Horizon.Core.AppContext` owns all managers and orchestrates initialization:
1. Loads JSON configs from `%APPDATA%\Horizon\config\`
2. Creates managers (CameraManager, UnifiedMotionManager, EziIOManager, CurrentMeasurementManager, LaserControllerManager, PneumaticManager, SafetyWatchManager)
3. Discovers process plugins via `ProcessPluginRegistry.DiscoverPlugins()`
4. Builds ProcessEngine device registry via reflection
5. SplashWindow runs hardware connections async, then MainWindow opens

Property `ILogSink? LogSink` is set by App.xaml.cs (Serilog adapter) — managers log via `LogSink?.Info/Warn/Error()` extension methods.

### Config System

`ConfigLoader.LoadJson<T>(dir, name)` with `.local.json` override (System.Text.Json, case-insensitive, comments/trailing commas allowed). Config dir: `%APPDATA%\Horizon\config\`.

Key config files: `io_devices.json`, `motor_devices.json`, `pneumatic_slides.json`, `safety_watch.json`, `current_measurement_devices.json`, `laser_controller_devices.json`.

### Hardware Adapters (all via interfaces)

| Domain | Interface | Adapters | SDK |
|---|---|---|---|
| Camera | ICameraHardware | PylonCameraAdapter, IDSPeakCameraAdapter, UEyeCameraAdapter | Basler Pylon, IDS Peak, IDS uEye (managed + P/Invoke) |
| Motion | IMotionController | ACSMotionController, PIMotionController | ACS.SPiiPlusNET6 (managed), PI_GCS2_DLL_x64 (P/Invoke) |
| IO | IIODevice | EziIODevice | EziMOTIONPlusE.dll (P/Invoke) |
| Pneumatic | IPneumaticSlide | PneumaticSlide | Uses IIODevice output pins |
| Current Measurement | ICurrentMeasurementDevice | Keithley6482Adapter | visa64.dll P/Invoke (NI-VISA runtime) |
| Laser Controller | ILaserControllerDevice | CLD101xAdapter | visa64.dll P/Invoke (Thorlabs CLD101x SCPI over USB) |

### Fastech Ezi-IO Register Layout

Standalone Ezi-IO modules use a **combined I/O register**: bits `[0..InputCount-1]` = inputs, bits `[InputCount..InputCount+OutputCount-1]` = outputs. SetOutput/ReadAllOutputs in `EziIODevice` handle the bit offset automatically. Motor IO (`FAS_SetIOOutput`) does not use this offset.

### TeachPointManager

`Horizon.Core.TeachPointManager` manages named products containing taught positions per motion device/axis. Products are persisted as JSON in `%APPDATA%\Horizon\config\teach-points\`. `TeachPointStore` handles file I/O. The active product is tracked separately. Used by TrainerPanel and motion command handlers for position recall.

### UserPreferences

`Horizon.Common.UserPreferences` — user-level settings (auto-open top view, crosshair, click-to-move, chart overlay, last-selected devices). Loaded/saved via `ConfigLoader` as `user_preferences.json` in the config directory.

### MeasurementHub

`Horizon.Core.MeasurementHub` — central pub/sub for measurement data. Channels are auto-created on first `Push(key, value, timestamp)`. Each `MeasurementChannel` is a lock-based circular buffer (500 samples). Channel key convention: `"{DeviceName}.CH{N}"` (e.g., `"Keithley-1.CH1"`). Consumers: CurrentReadingChart, OverlayWindow, CurrentChartPanel, LiveFeedPanel.

### Process Engine

`ProcessEngine` executes command sequences. `CommandDispatcher` routes commands by manager type (motion, io, pneumatic, camera, currentmeasurement, lasercontroller, system, context) to 8 `ICommandHandler` implementations. `ProcessContext` holds runtime variables as thread-safe dictionary (typed getters: `GetNumeric`, `GetString`, `GetBoolean`; `GetSnapshot()` for live debugging). Supports breakpoints via `ManualResetEventSlim` gate and step-by-step mode.

Callbacks: `OnStepStarted(index, device, method)`, `OnStepCompleted(index, success)`, `OnLoopIteration(index, current, total)`.

### Process Plugin System

Plugins define reusable process step templates. Each implements `IProcessPlugin` with metadata (`Id`, `DisplayName`, `Category`, `Description`, `Parameters`) and a `BuildSteps()` method that emits `ProcessStep`s via `ProcessBuilder`.

- **Discovery**: `ProcessPluginRegistry.DiscoverPlugins(assemblies)` finds plugins via reflection (parameterless constructor required)
- **Parameter metadata**: `PluginParamDef` with types: Text, Number, Boolean, Enum, FilePath, MotionDevice, CameraDevice, NamedPosition, Axis
- **Dependent params**: `DependsOnDevice` chains dropdowns (e.g., NamedPosition refreshes when MotionDevice changes)
- **UI binding**: `PropertyTable` control auto-generates forms from `PluginParamDef` metadata
- **Location**: `Horizon.Process/Plugins/` (15 built-in plugins across Motion, Camera, System, Variables categories)

### Expression Evaluator

`ExpressionEvaluator` in `Horizon.Process` — lightweight recursive-descent parser (`ref struct` on `ReadOnlySpan<char>`). Supports `+`, `-`, `*`, `/`, parentheses, float literals (with scientific notation), and variable references resolved from `ProcessContext`. Division by zero returns IEEE 754 Infinity.

Three usage patterns:
1. **Plugin**: `CalculateExpressionPlugin` evaluates expression → stores result in context variable
2. **Parameter prefix**: Any handler parameter with `expr:EXPRESSION` computes inline (e.g., `expr:distance * 0.5`)
3. **Log interpolation**: `{=EXPRESSION}` in SystemCommandHandler log messages (e.g., `Distance = {=posA - posB}`)

### ProcessProgram Persistence

`ProcessProgram` model with `ProcessProgramStep` entries (PluginId, Parameters dict, Enabled flag, Uid, CustomName). Serialized as JSON `.hproc` files in `%APPDATA%\Horizon\processes\`. Last-opened file tracked in `.lastprogram`.

### UI Panels (AvalonDock)

MainWindow creates document panels per hardware device: IOPanel, MotionPanel, PneumaticPanel, CurrentMeasurementPanel, CurrentChartPanel, LaserControllerPanel, LiveFeedPanel, CameraInfoWindow, OverlayWindow. Each panel polls its hardware independently (DispatcherTimer) and exposes LogMessage/StatusChanged events for the output log and hardware tree.

ProcessPanel includes a plugin catalog (TreeView by category), builder list with step highlighting (yellow=current, green=success, red=failure), PropertyTable for parameter editing, and a Context Watch DataGrid showing live variables during execution.

TrainerPanel is a 3-step wizard for position accuracy training: (1) teach bonding positions via corner capture, (2) teach reference points to establish a coordinate frame, (3) retrace/align by re-visiting references and computing corrected positions. Uses `TeachPointManager` + `TrainerModels.cs` in Core. See `docs/position-accuracy-trainer.md` for coordinate math details.

## Conventions

- Nullable enabled, implicit usings, AllowUnsafeBlocks in App only
- Thread safety: `ConcurrentDictionary` for device collections, `lock` for hardware access, `PeriodicTimer` for async polling
- Error enums per domain: `IOError`, `PneumaticError`, `MotionError`, `CurrentMeasurementError`, `LaserControllerError` — plus unified `ErrorCode` (hex-categorized) in Common
- Pin names use `StringComparer.OrdinalIgnoreCase` throughout
- Native DLLs (PI, Fastech, VISA, uEye) are copied to output via `<None Include="..." CopyToOutputDirectory="PreserveNewest">`
- VISA instruments (Keithley, Thorlabs CLD101x) share `Native/VisaNative.cs` P/Invoke pattern
- Logging: `ILogSink` interface in Core, `SerilogLogSink` adapter in App (rolling daily files + WPF output sink)
- NuGet: AvalonDock 4.72.1, SkiaSharp.Views.WPF 3.116.1

## Documentation

Detailed per-module docs live in `docs/modules/` (one .md per project). Architecture overview in `docs/architecture.md`. Trainer design docs in `docs/position-accuracy-trainer.md` and `docs/multi-device-trainer-design.md`.
