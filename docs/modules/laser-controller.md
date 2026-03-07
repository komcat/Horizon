# Horizon.LaserController

Laser controller subsystem for Thorlabs CLD101x combined laser diode driver + TEC controller. Communicates via SCPI over USB through NI-VISA.

## Dependencies

- **Horizon.Common** (project reference)
- **visa64.dll** (NI-VISA runtime, P/Invoke)

## Files

| File | Description |
|------|-------------|
| `LaserControllerManager.cs` | Manager: device registry, passthrough operations |
| `Adapters/CLD101xAdapter.cs` | VISA/USB adapter for Thorlabs CLD101x |
| `LaserControllerJsonLoader.cs` | Config loader from `laser_controller_devices.json` |
| `Interfaces/ILaserControllerDevice.cs` | Hardware abstraction interface |
| `Models/LaserControllerTypes.cs` | Error enum, config records, status record |
| `Native/VisaNative.cs` | P/Invoke wrapper for `visa64.dll` (same as CurrentMeasurement) |

## ILaserControllerDevice

```csharp
public interface ILaserControllerDevice
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }
    string DeviceName { get; }
    string ControllerType { get; }

    // TEC control
    LaserControllerError SetTemperature(double celsius);
    LaserControllerError ReadTemperature(out double celsius);
    LaserControllerError TECOn();
    LaserControllerError TECOff();
    bool IsTECOn { get; }

    // Laser control
    LaserControllerError SetLaserCurrent(double milliamps);
    LaserControllerError ReadLaserCurrent(out double milliamps);
    LaserControllerError LaserOn();
    LaserControllerError LaserOff();
    bool IsLaserOn { get; }

    LaserControllerStatus GetStatus();
}
```

## CLD101xAdapter

SCPI adapter for Thorlabs CLD101x over USB. The CLD101x has two output channels:

| Channel | SCPI Output | Function | SCPI Commands |
|---------|-------------|----------|---------------|
| Laser | OUTPUT1 | Laser diode current | `source1:current`, `sense3:current` |
| TEC | OUTPUT2 | Temperature control | `source2:temperature`, `sense2:temperature` |

### Connection

```
Connect()
  1. VisaNative.OpenDefaultRM(out rmSession)
  2. VisaNative.Open(rmSession, visaResource, out instrSession)
  3. VisaNative.SetAttribute(instrSession, TMO_VALUE, 2000ms)
  4. Send *CLS (clear status)
  5. QueryBoolState("output1:state?") → _laserOn
  6. QueryBoolState("output2:state?") → _tecOn
```

### TEC Control

| Method | SCPI Command |
|--------|-------------|
| `SetTemperature(celsius)` | `source2:temperature:spoint {celsius}` |
| `ReadTemperature(out celsius)` | `SENSe2:temperature:data?` |
| `TECOn()` | `output2:state on` |
| `TECOff()` | `output2:state off` |

### Laser Control

| Method | SCPI Command | Notes |
|--------|-------------|-------|
| `SetLaserCurrent(milliamps)` | `source1:current:level:amplitude {amps}` | Converts mA → A |
| `ReadLaserCurrent(out milliamps)` | `sense3:current:dc:data?` | Converts A → mA |
| `LaserOn()` | `output1:state on` | |
| `LaserOff()` | `output1:state off` | |

### GetStatus

Queries both channels and returns a complete snapshot:

```csharp
record LaserControllerStatus(
    bool Connected,
    bool TECOn,
    bool LaserOn,
    double CurrentTemperature,
    double SetpointTemperature,
    double CurrentLaserCurrent,    // in mA
    double SetpointLaserCurrent,   // in mA
    string LastError
);
```

### QueryBoolState

Sends a query, parses response — `"ON"` or `"1"` (case-insensitive) → `true`.

### Thread Safety

All operations wrapped in `lock (_lock)`.

## LaserControllerManager

Central manager with `ConcurrentDictionary<string, ILaserControllerDevice>` (case-insensitive).

### Device Factory

Supports controller type `"cld101x_direct"` → creates `CLD101xAdapter`.

### Passthrough Methods

All TEC and laser operations delegate to the device by name: `SetTemperature(name, celsius)`, `TECOn(name)`, `LaserOn(name)`, etc.

### Disposal

Implements `IDisposable`: `Dispose()` → `DisconnectAll()`.

## Config Format

```json
{
  "Devices": [
    {
      "Name": "Laser-1",
      "ControllerType": "cld101x_direct",
      "VisaResource": "USB0::0x1313::0x804F::M00123456::INSTR",
      "Enabled": true,
      "DefaultTemperature": 25.0,
      "DefaultLaserCurrent": 50.0
    }
  ]
}
```

## Error Codes

```csharp
enum LaserControllerError
{
    Success, DeviceNotFound, Disconnected,
    OperationFailed, CommunicationError, ParseError
}
```

## Shared VISA P/Invoke

Uses the same `VisaNative.cs` P/Invoke pattern as `Horizon.CurrentMeasurement` (identical file). Both modules require NI-VISA runtime to be installed.

## Thread Model

- `CLD101xAdapter`: `lock` guards all VISA session access
- `LaserControllerManager`: `ConcurrentDictionary` for device registry
- No polling loop — status is read on-demand from UI panel (500ms DispatcherTimer)
