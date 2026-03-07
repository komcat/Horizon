# Horizon.App

WPF shell application using AvalonDock for dockable panel management. Entry point for the Horizon system — initializes logging, creates AppContext, orchestrates hardware connections, and hosts all UI panels.

## Dependencies

- **Horizon.Core** (project reference) — AppContext, IAppContext, MeasurementHub
- **Horizon.Process** (project reference) — ProcessEngine, plugins
- **Horizon.IO**, **Horizon.Motion**, **Horizon.Pneumatic**, **Horizon.CurrentMeasurement**, **Horizon.LaserController** (project references for panel types)
- **AvalonDock 4.72.1** (NuGet) — dockable panel framework
- **SkiaSharp.Views.WPF 3.116.1** (NuGet) — hardware-accelerated rendering
- **Serilog** (NuGet) — structured logging

## Files

### Core Application

| File | Description |
|------|-------------|
| `App.xaml.cs` | Entry point: Serilog, Pylon SDK, AppContext lifecycle |
| `SplashWindow.xaml.cs` | Startup screen with device connection progress |
| `MainWindow.xaml.cs` | AvalonDock shell, hardware tree, panel management |

### Hardware Panels

| File | Poll Rate | Description |
|------|-----------|-------------|
| `IOPanel.xaml.cs` | 200ms | Digital I/O input/output control |
| `MotionPanel.xaml.cs` | 200ms | Axis positions, jog, named positions |
| `PneumaticPanel.xaml.cs` | 500ms | Extend/retract slides, sensor status |
| `CurrentMeasurementPanel.xaml.cs` | 500ms | Keithley channels, voltage source, fast poll |
| `LaserControllerPanel.xaml.cs` | 500ms | TEC + laser control, live readings |

### Camera Panels

| File | Description |
|------|-------------|
| `LiveFeedPanel.xaml.cs` | Real-time camera frame display (33ms render) |
| `CameraInfoPanel.xaml.cs` | Camera info, connect, exposure settings |
| `OverlayWindow.xaml.cs` | Floating camera view with chart overlay + click-to-move |

### Measurement & Visualization

| File | Description |
|------|-------------|
| `CurrentReadingChart.xaml.cs` | 2-channel realtime chart (WPF Polylines, 33ms render) |
| `CurrentChartPanel.xaml.cs` | Pop-out chart with device selector |

### Process Execution

| File | Description |
|------|-------------|
| `ProcessPanel.xaml.cs` | Visual process builder + execution |
| `RunPanel.xaml.cs` | Chain editor with deck/slot model |

### Manual Motion & Scanning

| File | Description |
|------|-------------|
| `JogPanel.xaml.cs` | Keyboard-driven jog interface |
| `TeachPointPanel.xaml.cs` | Teach, save, manage named positions |
| `HexScanPanel.xaml.cs` | Automated hexagonal grid scanning |

### Controls

| File | Description |
|------|-------------|
| `Controls/PropertyTable.xaml.cs` | Auto-generates forms from PluginParamDef metadata |

### Logging

| File | Description |
|------|-------------|
| `Logging/WpfOutputSink.cs` | Serilog sink → ObservableCollection for log ListBox |
| `Logging/SerilogLogSink.cs` | ILogSink → Serilog bridge |
| `Logging/LogEntry.cs` | View model for log display items |

### Dialogs

| File | Description |
|------|-------------|
| `ExpressionSyntaxWindow.xaml.cs` | Expression syntax help |
| `InputDialog.xaml.cs` | Simple text input |
| `CountdownDialog.xaml.cs` | Countdown timer display |

## App.xaml.cs — Entry Point

### OnStartup

1. **Serilog configuration** — rolling daily file (`%APPDATA%\Horizon\logs\horizon-{date}.log`), 7-day retention, Debug minimum, plus WPF output sink at Information level
2. **Pylon SDK environment** — find Pylon installation, set `PYLON_ROOT`, add Runtime/x64 to PATH, set `GENICAM_GENTL64_PATH`, call `AddDllDirectory()`
3. **Create AppContext** + assign `SerilogLogSink`
4. **Show SplashWindow** → `RunConnectionsAsync()`
5. **Show MainWindow** → close splash

### OnExit

1. `_appContext?.Dispose()` → `AppContext.Shutdown()` (reverse-order disposal)
2. `Log.CloseAndFlush()` → flush Serilog

## SplashWindow — Hardware Connection

### RunConnectionsAsync

1. `Task.Run(() => _context.Initialize())` — load configs, discover cameras, create managers
2. `BuildDeviceList()` — enumerate IO, Motion, CurrentMeasurement, Camera devices
3. **Sequential connection loop** — for each device:
   - Show "Connecting..." (amber)
   - `Task.Run(() => ConnectDevice(device))` — connect on thread pool
   - Show "Connected" (green) or "Failed" (red)
4. Display summary: `"{success} connected, {failed} failed"`, 800ms delay

### Connection Order

IO → Motion → CurrentMeasurement → Camera (sequential, not phased)

## MainWindow — Panel Management

### Loaded

1. Restore window position/size from `%APPDATA%\Horizon\window-state.txt`
2. Restore AvalonDock layout from `%APPDATA%\Horizon\dock-layout.xml`
3. Wire WPF output sink to log ListBox (auto-scroll)
4. Load registered cameras, refresh hardware tree

### Hardware Tree

TreeView with categories: Cameras, IO Devices, Motion Controllers, etc. Double-click creates/activates the corresponding panel as an AvalonDock document.

### Panel Creation

Each panel is created as a `LayoutDocument` with a unique `ContentId` (e.g., `io_DeviceName`, `motion_DeviceName`). Duplicate prevention: checks existing documents before creating new ones.

### Closing

Stops all 12 panel categories (see architecture.md), closes overlay window, saves window state and dock layout.

## Key Panel Details

### IOPanel

- Dynamically constructs input/output rows based on device pin count
- 200ms `DispatcherTimer` reads hardware state
- Toggle buttons for individual output pins
- Color-coded: green (ON), dim gray (OFF)

### MotionPanel

- Axis rows with current position, status (Moving/OnTarget/Idle)
- Jog buttons (±) with step size selector (1um to 10mm)
- Named position combo + Go button
- Stop/Zero per axis

### CurrentMeasurementPanel

- Channel rows for enabled channels only
- Passive poll (500ms) + async fast poll mode (NPLC=0.1, ~50-80 Hz)
- Pushes readings to MeasurementHub (`{DeviceName}.CH{N}`)
- Pop-out chart button creates `CurrentChartPanel`
- Voltage source control (set value, on/off)

### LiveFeedPanel

- SkiaSharp-based frame rendering at 33ms (30fps)
- RGB-to-BGRA32 conversion (unsafe pointers for performance)
- WriteableBitmap display
- Optional measurement overlay chart

### OverlayWindow

- Combined camera feed + SkiaSharp chart overlay
- Crosshair with pixel coordinates
- **Click-to-move**: loads `click_to_move.json` calibration, converts pixel offset to mm, triggers async XY move
- Z jog buttons with step size selector
- Device combo for measurement overlay

### ProcessPanel

- **Catalog**: TreeView of plugins grouped by category (searchable)
- **Builder**: ListBox with step list, visual indentation for loops, disabled steps show `//`
- **PropertyTable**: dynamic parameter editing from plugin metadata
- **Context Watch**: DataGrid showing live variables during execution (highlights changes)
- **File operations**: New/Open/Save `.hproc` files
- **Execution**: builds flat step list, maps to builder indices for UI highlighting, runs via `Task.Run`
- **Step control**: Run/Stop/Continue, step-by-step mode, breakpoints

### JogPanel

- Device selector (toggle buttons per connected device)
- 13 step size presets (0.1um to 2mm)
- Arrow buttons: X±, Y±, Z±, U±, V±, W± (UVW conditional on axis count)
- **Keyboard bindings**: W/S (Y), A/D (X), R/F (Z), Q/E (step cycle), Esc (stop all)
- Green flash feedback on button press (150ms)
- Keyboard focus indicator

### TeachPointPanel

- Product management: create, rename, delete, set active
- DataGrid with editable X/Y/Z/U/V/W columns
- Teach Current: capture live position
- Go Point: move to selected point
- Live position display (200ms poll)

### HexScanPanel

- Device/channel selectors
- Coarse/Fine presets
- Results DataGrid: step index, axis, direction, step size, position, value, peak flag
- Summary: baseline, peak, improvement %
- Progress bar with percentage

## PropertyTable Control

Auto-generates WPF controls from `PluginParamDef` metadata:

| ParamType | WPF Control |
|-----------|-------------|
| MotionDevice / CameraDevice | ComboBox (populated from managers) |
| NamedPosition / Axis | ComboBox (dependent, refreshes on device change) |
| Number | TextBox + optional unit suffix label |
| Boolean | ComboBox (True/False) |
| Enum | ComboBox (from Options array) |
| FilePath | TextBox + Browse button (OpenFolderDialog) |
| Text | TextBox |

Groups parameters by category with header + separator. Step UID (read-only) and custom name textbox at top.

`ValuesChanged` event fires when any control changes. `GetValues()` extracts current values as `Dictionary<string, string>`.

## Logging Components

### WpfOutputSink

Serilog sink bridging to WPF:
- `ObservableCollection<LogEntry>` with 5000 max entries (FIFO)
- `CollectionViewSource` with filter support (Info/Warning/Error toggles)
- `Initialize(Dispatcher)` must be called from MainWindow
- Events dispatched via `Dispatcher.BeginInvoke(Background priority)`

### LogEntry

Immutable view model: `Timestamp` (HH:mm:ss.fff), `Level`, `Message`, `Foreground` brush
- Frozen static brushes: Info (light gray), Warning (amber), Error (red)
- Prepends `[WARN]`/`[ERROR]`/`[FATAL]` prefix based on level

## Architectural Patterns

1. **DispatcherTimer polling**: all panels use 200-500ms timers for hardware status
2. **Task.Run for hardware**: connect, move, read operations dispatched to thread pool
3. **MeasurementHub pub/sub**: panels push data; charts subscribe by channel key
4. **Pre-allocated buffers**: charts and frame renderers allocate once (zero GC per frame)
5. **Dynamic UI construction**: IOPanel, MotionPanel, PropertyTable build controls at runtime
6. **Color-coded status**: green=OK, amber=busy, red=error, gray=off (consistent across all panels)
7. **ContentId-based dedup**: AvalonDock documents identified by prefix + device name
