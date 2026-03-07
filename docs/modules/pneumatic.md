# Horizon.Pneumatic

Pneumatic solenoid slide control via digital I/O output pins. Each slide drives a single solenoid valve and reads two position sensors (extended/retracted) through the IO subsystem.

## Dependencies

- **Horizon.Common** (project reference)
- **Horizon.IO** (project reference) — `PneumaticSlide` operates through `EziIOManager`

## Files

| File | Description |
|------|-------------|
| `PneumaticSlide.cs` | `IPneumaticSlide` implementation |
| `PneumaticManager.cs` | Manager: slide registry, batch operations |
| `PneumaticJsonLoader.cs` | Config loader from `pneumatic_slides.json` |
| `Interfaces/IPneumaticSlide.cs` | Hardware abstraction interface |
| `Models/PneumaticTypes.cs` | Enums, config records, status record |

## IPneumaticSlide

```csharp
public interface IPneumaticSlide
{
    PneumaticError Extend();
    PneumaticError Retract();
    PneumaticError ExtendAndWait(int pollIntervalMs = 50);
    PneumaticError RetractAndWait(int pollIntervalMs = 50);
    PneumaticStatus GetStatus();
    PneumaticPosition GetPosition();
    string SlideName { get; }
}
```

## PneumaticSlide

Each slide maps to three IO pins via `IOPinReference` (DeviceName + PinName):

| Pin | Direction | Purpose |
|-----|-----------|---------|
| `Output` | Output | Solenoid valve control |
| `ExtendedSensor` | Input | Extended position sensor |
| `RetractedSensor` | Input | Retracted position sensor |

### Extend/Retract

Sets the output pin ON (extend) or OFF (retract) via `EziIOManager.SetOutputByName()`. Records timestamp via `Stopwatch.GetTimestamp()`.

### ExtendAndWait / RetractAndWait

Polls `GetPosition()` at configurable interval (default 50ms) until the target position is reached or timeout expires (from config `TimeoutMs`, default 5000ms).

### GetPosition Logic

Reads both sensor pins and determines position:

| Extended Sensor | Retracted Sensor | Position |
|-----------------|-------------------|----------|
| ON | OFF | Extended |
| OFF | ON | Retracted |
| ON | ON | HardwareError |
| OFF | OFF | Unknown (transitioning) |

### GetStatus

Returns `PneumaticStatus` record:
```csharp
record PneumaticStatus(
    PneumaticPosition Position,
    bool OutputActive,
    bool ExtendedSensorActive,
    bool RetractedSensorActive,
    long LastMoveTimeMs,
    bool Valid
);
```

## PneumaticManager

Simple dictionary-based manager (`Dictionary<string, IPneumaticSlide>`, `OrdinalIgnoreCase`).

| Method | Description |
|--------|-------------|
| `AddSlide(config, ioManager)` | Create `PneumaticSlide` and register |
| `Extend(name)` / `Retract(name)` | Delegate to slide |
| `ExtendAndWait(name)` / `RetractAndWait(name)` | Blocking delegate |
| `GetSlide(name)` | Get slide instance |
| `GetAllSlideNames()` | List all registered names |
| `StopAll()` | Retract all slides |

Does NOT implement `IDisposable` — no active resources to clean up.

## Config Format

```json
{
  "Slides": [
    {
      "Name": "Clamp-1",
      "TimeoutMs": 5000,
      "Output": { "DeviceName": "IO-Module-1", "PinName": "Valve1" },
      "ExtendedSensor": { "DeviceName": "IO-Module-1", "PinName": "ClampExtended" },
      "RetractedSensor": { "DeviceName": "IO-Module-1", "PinName": "ClampRetracted" }
    }
  ]
}
```

## Error Codes

```csharp
enum PneumaticError
{
    Success, SlideNotFound, IOManagerNull, DeviceNotFound,
    PinNotFound, Timeout, AlreadyAtPosition, HardwareError, OperationFailed
}
```

## PneumaticPosition

```csharp
enum PneumaticPosition
{
    Extended, Retracted, Extending, Retracting,
    Unknown, HardwareError, Timeout
}
```

## Critical Dependency

**IO must be initialized before Pneumatic.** `PneumaticJsonLoader.LoadSlides()` receives an `EziIOManager` reference to resolve device/pin names. In `AppContext.Initialize()`, `EziIOManager` is created at step 4, while `PneumaticManager` is created at step 8.

## Thread Model

Delegates all hardware access to `EziIOManager`, inheriting its `lock`-based thread safety. No independent threads or timers. Blocking waits use `Thread.Sleep(pollIntervalMs)` spin loops.
