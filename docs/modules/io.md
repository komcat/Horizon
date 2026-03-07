# Horizon.IO

Digital I/O subsystem for Fastech Ezi-IO modules and motor controller built-in I/O. Provides pin-level input reading and output control with named pin mapping.

## Dependencies

- **Horizon.Common** (project reference)
- **EziMOTIONPlusE.dll** (native, copied to output)

## Files

| File | Description |
|------|-------------|
| `EziIODevice.cs` | `IIODevice` implementation for Fastech hardware |
| `EziIOManager.cs` | Manager: device registry, polling loop, events |
| `EziIOJsonLoader.cs` | Config loader from `io_devices.json` |
| `Interfaces/IIODevice.cs` | Hardware abstraction interface |
| `Models/IOTypes.cs` | Error enum, config records, status record |
| `Native/EziNative.cs` | P/Invoke wrapper for `EziMOTIONPlusE.dll` |
| `Native/EziDefines.cs` | Device type constants for Ezi-IO variants |

## IIODevice

```csharp
public interface IIODevice
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }

    IOError ReadInputs(out uint inputs);
    IOError ReadInputs(out uint inputs, out uint latch);
    IOError SetOutput(int pin, bool state);
    IOError SetOutputByName(string pinName, bool state);
    IOError ReadAllOutputs(out uint outputs);

    string DeviceName { get; }
    int InputCount { get; }
    int OutputCount { get; }

    void SetPinName(bool isInput, int pin, string name);
    int? GetPinByName(string name);
    string? GetPinName(bool isInput, int pin);
}
```

## EziIODevice

Handles two device types via `EziDeviceType` enum:

### IOModule (Standalone Ezi-IO)

Uses a **combined I/O register** where:
- Bits `[0..InputCount-1]` = inputs
- Bits `[InputCount..InputCount+OutputCount-1]` = outputs

`SetOutput()` applies bit offset: `1u << (pin + InputCount)`
`ReadAllOutputs()` shifts down: `outputs >>= InputCount`

### MotorIO (Motor Controller Built-in)

Separate input/output registers — no bit offset:
- `FAS_GetIOInput()` / `FAS_GetIOOutput()` for reading
- `FAS_SetIOOutput(setMask, clearMask)` for writing

### Pin Name Mapping

Two `Dictionary<string, int>` (one for inputs, one for outputs) with `StringComparer.OrdinalIgnoreCase`. Pins can be addressed by name or index.

### Thread Safety

All hardware operations wrapped in `lock (_lock)`.

## EziIOManager

Central manager for multiple IO devices.

### Device Registry

`ConcurrentDictionary<string, IIODevice>` keyed by device name (case-insensitive).

Methods: `AddDevice()`, `RemoveDevice()`, `ConnectAll()`, `DisconnectAll()`, `ConnectDevice()`, `GetDevice()`, `GetAllDeviceNames()`

### Polling Loop

`PeriodicTimer`-based async loop (configurable interval, default 100ms):

```
StartPolling(intervalMs)
  → For each connected device:
    → ReadInputs(out inputs, out latch)
    → ReadAllOutputs(out outputs)
    → Fire OnIOStatusUpdated(deviceName, IOStatus)
```

`IOStatus` record: `(uint Inputs, uint Latch, uint Outputs, bool Valid)`

`StopPolling()` cancels the timer and waits for the poll task to complete.

### Disposal

Implements `IDisposable`: `Dispose()` → `StopPolling()` → `DisconnectAll()`

## EziIOJsonLoader

Static `LoadDevices(EziIOManager, configDir)`:
- Loads `io_devices.json` via `ConfigLoader`
- Parses `EziDeviceType` enum from JSON (case-insensitive, defaults to IOModule)
- Configures pin names for both inputs and outputs
- Returns count of devices loaded

### Config Format

```json
{
  "Devices": [
    {
      "Device": {
        "DeviceId": 0,
        "Name": "IO-Module-1",
        "IpAddress": "192.168.0.2",
        "InputCount": 16,
        "OutputCount": 16,
        "DeviceType": "IOModule"
      },
      "InputPins": [
        { "Pin": 0, "Name": "Sensor1" }
      ],
      "OutputPins": [
        { "Pin": 0, "Name": "Valve1" }
      ]
    }
  ]
}
```

## Native P/Invoke (EziNative.cs)

DLL: `EziMOTIONPlusE.dll`

| Function | Description |
|----------|-------------|
| `FAS_Connect(ip, boardId)` | Connect to device by IP + board ID |
| `FAS_GetInput(boardId, out inputs)` | Read standalone IO inputs |
| `FAS_GetOutput(boardId, out outputs)` | Read standalone IO outputs |
| `FAS_SetOutput(boardId, outputs)` | Set standalone IO outputs |
| `FAS_ClearLatch(boardId)` | Clear input latch register |
| `FAS_GetIOInput(boardId, out inputs)` | Read motor controller inputs |
| `FAS_GetIOOutput(boardId, out outputs)` | Read motor controller outputs |
| `FAS_SetIOOutput(boardId, setMask, clearMask)` | Set/clear motor controller outputs |
| `FAS_GetSlaveInfoEx(boardId, ...)` | Query device info |
| `FAS_IsSlaveExist(boardId)` | Check if device is present |

Return codes: `FMM_OK=0`, `FMM_NOT_OPEN=1`, `FMC_DISCONNECTED=5`, `FMC_TIMEOUT_ERROR=6`

### Device Types (EziDefines.cs)

Supported variants: IN16, IN32, IO16, I8O8, I16O16, OUT16, OUT32 — each with fixed input/output counts.

## Thread Model

- `EziIODevice`: `lock` on all hardware calls
- `EziIOManager`: `ConcurrentDictionary` for device registry, `PeriodicTimer` for async polling
- Pin name lookups are case-insensitive (`StringComparer.OrdinalIgnoreCase`)
