# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

- **Solution**: `Horizon.sln` (10 projects, all net8.0, x64 only)
- **WPF app entry**: `Horizon.App` (net8.0-windows)
- **User compiles themselves** — do not run `dotnet build` or `msbuild`
- **User tests themselves** — no test projects exist; skip test commands
- No CI/CD pipelines configured

## Architecture

Modular hardware control system (WPF + AvalonDock) for cameras, motion stages, digital I/O, pneumatic slides, current measurement, process sequencing, and safety monitoring.

### Dependency Graph

```
Horizon.App (WPF shell)
  └─ Horizon.Core (AppContext wires all managers)
       ├─ Horizon.Process (CommandDispatcher + ProcessEngine)
       │    ├─ Horizon.Camera
       │    ├─ Horizon.Motion
       │    ├─ Horizon.Pneumatic ─── Horizon.IO
       │    ├─ Horizon.IO
       │    ├─ Horizon.CurrentMeasurement
       │    └─ Horizon.Safety ────── Horizon.IO
       └─ Horizon.Common (ErrorCode, Result<T>, ConfigLoader)
```

### AppContext (DI Container)

`Horizon.Core.AppContext` owns all managers and orchestrates initialization:
1. Loads JSON configs from `%APPDATA%\Horizon\config\`
2. Creates managers (CameraManager, UnifiedMotionManager, EziIOManager, CurrentMeasurementManager, PneumaticManager, SafetyWatchManager)
3. Builds ProcessEngine device registry via reflection
4. SplashWindow runs hardware connections async, then MainWindow opens

### Config System

`ConfigLoader.LoadJson<T>(dir, name)` with `.local.json` override (System.Text.Json, case-insensitive, comments/trailing commas allowed). Config dir: `%APPDATA%\Horizon\config\`.

Key config files: `io_devices.json`, `motor_devices.json`, `pneumatic_slides.json`, `safety_watch.json`, `current_measurement_devices.json`.

### Hardware Adapters (all via interfaces)

| Domain | Interface | Adapters | SDK |
|---|---|---|---|
| Camera | ICameraHardware | PylonCameraAdapter, IDSPeakCameraAdapter | Basler Pylon, IDS Peak (managed) |
| Motion | IMotionController | ACSMotionController, PIMotionController | ACS.SPiiPlusNET6 (managed), PI_GCS2_DLL_x64 (P/Invoke) |
| IO | IIODevice | EziIODevice | EziMOTIONPlusE.dll (P/Invoke) |
| Pneumatic | IPneumaticSlide | PneumaticSlide | Uses IIODevice output pins |
| Current Measurement | ICurrentMeasurementDevice | Keithley6482Adapter | Ivi.Visa (NI-VISA runtime) |

### Fastech Ezi-IO Register Layout

Standalone Ezi-IO modules use a **combined I/O register**: bits `[0..InputCount-1]` = inputs, bits `[InputCount..InputCount+OutputCount-1]` = outputs. SetOutput/ReadAllOutputs in `EziIODevice` handle the bit offset automatically. Motor IO (`FAS_SetIOOutput`) does not use this offset.

### Process Engine

`ProcessEngine` executes command sequences. `CommandDispatcher` routes commands by manager type (motion, io, pneumatic, camera, currentmeasurement, system) to `ICommandHandler` implementations. Supports breakpoints via `ManualResetEventSlim` gate.

### UI Panels (AvalonDock)

MainWindow creates document panels per hardware device: IOPanel, MotionPanel, PneumaticPanel, CurrentMeasurementPanel, LiveFeedPanel, CameraInfoWindow. Each panel polls its hardware independently (DispatcherTimer) and exposes LogMessage/StatusChanged events for the output log and hardware tree.

## Conventions

- Nullable enabled, implicit usings, AllowUnsafeBlocks in App only
- Thread safety: `ConcurrentDictionary` for device collections, `lock` for hardware access, `PeriodicTimer` for async polling
- Error enums per domain: `IOError`, `PneumaticError`, `MotionError`, `CurrentMeasurementError` — plus unified `ErrorCode` (hex-categorized) in Common
- Pin names use `StringComparer.OrdinalIgnoreCase` throughout
- Native DLLs (PI, Fastech) are copied to output via `<None Include="..." CopyToOutputDirectory="PreserveNewest">`
