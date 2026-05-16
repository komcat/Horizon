# Layer 1 — Hardware Engine Design

> Status: **IMPLEMENTED** — design 2026-05-15, built and verified on the bench.
> All eight domains of §11 are wired behind `IHorizonEngine`.
> Scope: the bottom layer only. Builds on `docs/headless-engine-concept.md`.

---

## 1. Context

Horizon today welds three concerns into `Horizon.Core.AppContext` (882 lines):
hardware control, the product/process engines, and product data. A front-end
can reach a raw `EziIOManager` *or* the `ProcessEngine` — there is no boundary.

The plan: a clean **3-layer** split.

```
Layer 3  Front-ends (WPF, console, future web)   → depend on interfaces only
Layer 2  Product / Process module                → depends on IHorizonEngine
Layer 1  Hardware Engine  ← THIS DOCUMENT        → owns all hardware
```

This document covers **Layer 1 only**. Layers 2 and 3 are deliberately ignored
here. The engine is built and proven first; the upper layers are easier once
the foundation is verified.

A minimal **console host** (Spectre.Console) is built alongside Layer 1 as its
test rig — not a product. Because the console references *only* Layer 1, the
moment it compiles and drives hardware, the engine is provably standalone.

## 2. Decisions locked in this discussion

| Decision | Choice | Consequence |
|---|---|---|
| Recreate scope | **Keep existing adapters; recreate the composition/facet layer** | Adapter assemblies (`Horizon.Motion`, `Horizon.Camera`, `Horizon.IO`, …) and their `Native/` P/Invoke stay as-is — they are SDK-validated. Only the managers + engine wiring are recreated. Lowest risk; §8 quirks still apply. |
| Engine interface style | **Async (Task-based)** | Every facet method is `async`. Needs a concurrency model (§5). |
| Mock / simulation adapters | **No — real hardware only** | Console can only be tested on the bench. Development is domain-by-domain (§9). |
| Vision (HALCON) | **Inside Layer 1** | Vision is an engine facet; trained models are Layer 2 data passed in by reference. |

## 3. Scope

**In Layer 1:** Motion, IO, Pneumatic, Current Measurement, Camera, Vision
(HALCON), Laser Controller; Safety watchdog + Motion interlocks + execution
lock (all hardware *arbitration*); raw measurement pub/sub; hardware config
loading; per-domain status poll loops and events.

**Out (Layer 2+, not in this document):** ProcessEngine, RecipeEngine, plugins,
ProductManager, TeachPointManager, teach points / named positions, dispense,
trainer, calibration.

**Motion is coordinate-only.** The engine moves to **coordinates** —
single-axis (`MoveAbsoluteAsync`, `MoveRelativeAsync`) or multi-axis
(`MoveToPositionAsync` with X,Y,Z[,U,V,W]). It never resolves a *name* like
`"PickupPosition"` — name → coordinate resolution is a Layer 2 concern. As
built, the engine reads `motor_devices_config.json` (hardware only: controller
type, IP, axes); position data lives in separate Layer 2 files. The legacy
`motor_devices.json` (hardware + embedded positions) is read only by the
legacy WPF app, never by the engine.

## 4. Project structure

```
Horizon.Engine              ← composition root, implements IHorizonEngine
   references ↓
Horizon.Hardware.Abstractions   interfaces: IMotionController, ICameraHardware,
                                IIODevice, IVisionProcessor, …
Horizon.Motion / Camera / Vision / IO / Pneumatic /
   CurrentMeasurement / LaserController            ← adapters, one asm per domain
Horizon.Safety              watchdog + interlocks + execution lock
Horizon.Common              Result<T>, ErrorCode, ConfigLoader  (kept as-is)

Horizon.Console             ← Layer-1-only test host, Spectre.Console
```

`Horizon.Console` references **only `Horizon.Engine`**. That single reference
is the boundary test.

## 5. Concurrency model

Hardware SDKs are generally **not thread-safe per device**. An async public
surface invites concurrent calls (a `MoveAsync` while a status poll reads
position). Therefore:

- Public facet methods are `async`, every one takes a `CancellationToken`.
- **Each device owns a serialization primitive** (`SemaphoreSlim(1,1)` or a
  single-consumer command queue). Calls to the *same* device serialize; calls
  to *different* devices run in parallel.
- A motion call flows: `acquire device lock → interlock check → SDK call →
  release`. The **engine** enforces ordering — never the caller.
- Cancelling a move must invoke the SDK's stop.
- Blocking SDK calls are wrapped, but **not** with a blind `Task.Run` per call —
  they run on the device's serialized executor.

## 6. Lifecycle

```csharp
Task InitializeAsync(CancellationToken ct);          // load config, build devices — no I/O
Task ConnectAllAsync(IProgress<string> progress, CancellationToken ct);
HardwareStatusSnapshot GetStatusSnapshot();          // SYNCHRONOUS — cached, never touches hardware
Task ShutdownAsync();                                // stop motion, disconnect, dispose
```

`GetStatusSnapshot()` stays synchronous even on an async engine: it returns
cached state maintained by the internal poll loop. The console's live status
table reads it; it must never block.

**Internal poll loop:** the engine runs one background loop per domain (or
device) that reads status, updates the cached snapshot, and raises
`StatusChanged`. Front-ends subscribe — no `DispatcherTimer`, no front-end
polling. Built once here, reused by every future front-end.

## 7. Interface surface

### 7.1 Root — faceted, not a flat god-interface

```csharp
public interface IHorizonEngine : IAsyncDisposable
{
    IMotionFacet       Motion       { get; }
    IIOFacet           IO           { get; }
    IPneumaticFacet    Pneumatic    { get; }
    ICameraFacet       Camera       { get; }
    IVisionFacet       Vision       { get; }
    IMeasurementFacet  Measurement  { get; }
    ILaserFacet        Laser        { get; }
    ISafetyFacet       Safety       { get; }

    Task InitializeAsync(CancellationToken ct = default);
    Task ConnectAllAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    HardwareStatusSnapshot GetStatusSnapshot();
    Task ShutdownAsync();

    event Action<HardwareStatusChanged>? StatusChanged;
    event Action<LogEntry>?              LogEmitted;
}
```

A `MotionPanel` (Layer 3) depending on `IMotionFacet` *physically cannot* reach
IO — modularity enforced by the compiler.

### 7.2 Motion facet — fully fleshed out (template for the rest)

```csharp
public interface IMotionFacet
{
    IReadOnlyList<string> DeviceNames { get; }
    MotionDeviceStatus    GetStatus(string device);          // cached
    bool                  IsConnected(string device);

    Task<Result> ConnectAsync(string device, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string device);

    Task<Result> MoveAbsoluteAsync(string device, string axis, double position,
                                   CancellationToken ct = default);
    Task<Result> MoveRelativeAsync(string device, string axis, double distance,
                                   CancellationToken ct = default);
    Task<Result> MoveAbsoluteMultiAsync(string device, PositionStruct target,
                                        CancellationToken ct = default);
    Task<Result> HomeAsync(string device, string axis, CancellationToken ct = default);
    Task<Result> StopAsync(string device);                  // immediate, bypasses queue
    Task<Result> StopAllAsync();

    Task<Result<PositionStruct>> GetPositionAsync(string device);
    Task<Result> SetVelocityAsync(string device, double velocity);
    Task<Result<double>> GetVelocityAsync(string device);

    event Action<string /*device*/, PositionStruct>? PositionChanged;
}
```

### 7.3 Other facets — surface sketch

- **IIOFacet** — `SetOutputAsync(device, pin|name, state)`, `ReadInputsAsync`,
  `ReadAllOutputsAsync`, `InputChanged` event. Honors the Ezi-IO combined
  register offset internally (§8).
- **IPneumaticFacet** — `ExtendAsync` / `RetractAsync` / `…AndWaitAsync`,
  `GetSlideState`, `SlideStateChanged`. Sits on `IIOFacet`.
- **ICameraFacet** — `StartGrabbingAsync` / `StopGrabbingAsync`, `SnapAsync`,
  `ApplyExposureSettingsAsync`, `event FrameReady(camera, byte[], PixelFormat)`.
  Frames are **raw bytes** — no `BitmapSource`; each front-end converts.
- **IVisionFacet** — `FindPatternAsync(camera, modelRef, …)`,
  `MeasureAsync`, `AlignAsync`. Takes a **model reference** (path/handle);
  trained HALCON models are Layer 2 product data passed in. HALCON handles
  stay in-process — vision is correctly fused with the camera.
- **IMeasurementFacet** — Keithley: `ReadCurrentAsync`, `ReadAllChannelsAsync`,
  `SetVoltageAsync`, polling control, `event Sample(channel, value, ts)`.
- **ILaserFacet** — CLD101x: `SetTemperatureAsync`, `ReadTemperatureAsync`,
  `TecOn/OffAsync`, `SetLaserCurrentAsync`, `LaserOn/OffAsync`.
- **ISafetyFacet** — **read + acknowledge only**: `GetSafetyState()`,
  `AcknowledgeAsync()`, `event SafetyTripped(input, severity)` /
  `SafetyCleared`. Enforcement and interlock arbitration are *internal* to the
  engine — not exposed for callers to bypass.

## 8. Adapter handling — risk note

**As built, the existing adapters were kept** (§2): the engine wraps the
`Horizon.*` hardware modules unchanged. No P/Invoke was rewritten — which
sidesteps the risk below entirely. The note is retained as the rationale.

A mistyped `[DllImport]` signature is **not a compile error** — it is silent
memory corruption that surfaces as a random crash hours later. The risk lives
in the **P/Invoke** adapters: PI `PI_GCS2_DLL_x64`, Fastech `EziMOTIONPlusE`,
VISA `visa64.dll`, uEye. Had they been rewritten, the mitigations would have
been mandatory:

- Copy the `[DllImport]` declaration blocks **verbatim** from the existing
  `Native/` files (`PIGcs2.cs`, `EziNative.cs`, `VisaNative.cs`, uEye). Rewrite
  only the code *around* them.
- Carry these known quirks forward as written requirements — do **not**
  rediscover them by debugging on the bench:
  - **Ezi-IO combined I/O register:** inputs occupy bits `[0..InputCount-1]`,
    outputs are offset by `InputCount`. Motor IO (`FAS_SetIOOutput`) does **not**
    use the offset.
  - **IDS Peak** has no framerate control (Pylon does).
  - **Camera connect defaults:** AutoGain off, Gain 0, AutoExposure off,
    Exposure 10 ms, FrameRate 10 fps — applied on every connect.

## 9. Console test host (Spectre.Console)

`net8.0-windows`, **no `UseWPF`**. A minimal command/menu loop over
`IHorizonEngine` — a test rig, not a product; do not over-invest.

| Need | Spectre.Console feature |
|---|---|
| Pick a domain / device | `SelectionPrompt<T>` |
| Live status grid (auto-refresh) | `Table` + `Live`, fed by `StatusChanged` |
| Connect progress | `Status` / `Progress` ← `ConnectAllAsync(IProgress<string>)` |
| Connected/fault coloring | `AnsiConsole.MarkupLine` |

Cannot run while the WPF app is open — both processes would fight for the same
hardware (one-process rule). Acceptable for a diagnostic tool.

## 10. Error model

Keep `Horizon.Common`'s `Result<T>` / `ErrorCode` / `ErrorCategory` and
`ConfigLoader` as-is — domain-neutral and already good. Async methods return
`Task<Result>` / `Task<Result<T>>`. Expected hardware failures (timeout,
not-connected) return a failed `Result`; exceptions are reserved for programmer
error.

Logging is delivered through the engine's `LogEmitted` event
(`event Action<LogEntry>`) — there is no `ILogSink` interface. The console
subscribes and writes to stdout; a future WPF front-end subscribes and routes
to Serilog. `LogEntry` carries level, timestamp, source, and message.

## 11. Bring-up order — COMPLETE

All eight steps were built and verified on the bench, one commit each. Each
step = facet + engine wiring + console menu section; the console was never
broken and references only `Horizon.Engine` throughout.

| # | Step | Status |
|---|------|--------|
| 1 | **Engine skeleton** — `IHorizonEngine`, lifecycle, `PollLoop`, events, config | ✅ done |
| 2 | **IO** — Fastech Ezi-IO behind `IIOFacet` | ✅ done |
| 3 | **Motion** — PI GCS2 + ACS SPiiPlus behind `IMotionFacet` (per-device `SemaphoreSlim`) | ✅ done |
| 4 | **Pneumatic** — IO-driven slides behind `IPneumaticFacet` | ✅ done |
| 5 | **Current Measurement + Laser** — Keithley + CLD101x (VISA) | ✅ done |
| 6 | **Camera** — Pylon / IDS / uEye; raw `FrameReady` stream | ✅ done |
| 7 | **Vision** — minimal HALCON bridge over camera frames | ✅ done |
| 8 | **Safety + Interlocks** — watchdog enforcement + interlock gating | ✅ done |

Outcome: all eight domains are wired behind `IHorizonEngine`; `Horizon.Console`
drives the full engine headless. Adapters were **kept** (not rewritten) — the
engine wraps the existing `Horizon.*` hardware modules. `InterlockService` was
extracted into a shared `Horizon.Interlock` project (§12). Vision shipped as a
minimal bridge (image stats + blob count) — real inspection algorithms are
deferred to a future requirement.

## 12. Resolved decisions

- **Device addressing — per-domain.** Motion uses name+axis, IO uses name+pin,
  camera uses id. No forced unified device handle.
- **`Horizon.Engine` is one assembly.** Engine-core and the per-domain facets
  ship together; the per-domain *adapter* assemblies (`Horizon.Motion`, etc.)
  stay separate as before.
- **Config layout unchanged.** Keep the `%APPDATA%\Horizon\config\*.json`
  filenames and per-domain JSON files as-is.
- **No `ILogSink` interface.** Logging is the engine's `LogEmitted` event
  (`event Action<LogEntry>`); front-ends subscribe. The old `ILogSink`
  abstraction is dropped.
- **Interlocks in a shared project.** `InterlockService` + `InterlockTypes`
  were moved from `Horizon.Core` into a new `Horizon.Interlock` project,
  referenced by both the engine and legacy `Horizon.Core`. The namespace
  (`Horizon.Core.Interlock`) was kept so legacy code needed no edits.
- **Adapters kept, not rewritten.** Each facet wraps the existing `Horizon.*`
  hardware module unchanged; only the composition/facet layer is new.

## 13. As-built notes

- **Concurrency:** Motion uses a per-device `SemaphoreSlim` (PI/ACS adapters
  are not thread-safe). IO, Pneumatic, Measurement, Laser adapters lock
  internally — no facet semaphore. `StopAsync` bypasses the lock.
- **Poll loops:** `PollLoop` (engine scaffold) drives Motion, Pneumatic, Laser,
  Camera. IO and Measurement reuse their managers' built-in poll loops.
- **Vision:** minimal HALCON bridge — `AnalyzeFrame` (gray min/mean/max) and
  `CountBlobs` (threshold + connection). Degrades gracefully if HALCON or its
  license is absent. `Horizon.Console` carries the `MVTec.HalconDotNet-Windows`
  native package.
- **Console:** `Horizon.Console` (Spectre.Console) references **only**
  `Horizon.Engine` — the Layer 1 boundary, verified across all eight steps.
- **Next:** Layer 2 (product/process) and Layer 3 (front-ends) build against
  `IHorizonEngine` — see `docs/headless-engine-concept.md`.
