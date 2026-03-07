# Horizon.CurrentMeasurement

Current measurement subsystem for Keithley 6482 dual-channel picoammeter. Communicates via GPIB through NI-VISA.

## Dependencies

- **Horizon.Common** (project reference)
- **visa64.dll** (NI-VISA runtime, P/Invoke)

## Files

| File | Description |
|------|-------------|
| `CurrentMeasurementManager.cs` | Manager: device registry, polling, events |
| `Adapters/Keithley6482Adapter.cs` | VISA/GPIB adapter for Keithley 6482 |
| `CurrentMeasurementJsonLoader.cs` | Config loader from `current_measurement_devices.json` |
| `Interfaces/ICurrentMeasurementDevice.cs` | Hardware abstraction interface |
| `Models/CurrentMeasurementTypes.cs` | Error enum, config records, status record |
| `Native/VisaNative.cs` | P/Invoke wrapper for `visa64.dll` |

## ICurrentMeasurementDevice

```csharp
public interface ICurrentMeasurementDevice
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }

    CurrentMeasurementError ReadCurrent(int channel, out double current);
    CurrentMeasurementError ReadAllChannels(out double[] currents);

    CurrentMeasurementError GetIdentity(out string identity);
    CurrentMeasurementError Reset();

    CurrentMeasurementError SetVoltage(double volts);
    CurrentMeasurementError GetVoltage(out double volts);
    CurrentMeasurementError SetVoltageEnabled(bool enabled);

    CurrentMeasurementError ConfigureFastMode();
    CurrentMeasurementError ConfigureDefaultMode();

    string DeviceName { get; }
    int ChannelCount { get; }
    List<int> EnabledChannels { get; }
}
```

## Keithley6482Adapter

VISA GPIB adapter wrapping `visa64.dll` P/Invoke calls.

### Connection

```
Connect()
  1. VisaNative.OpenDefaultRM(out rmSession)
  2. VisaNative.Open(rmSession, visaResource, out instrSession)
  3. VisaNative.SetAttribute(instrSession, TMO_VALUE, 2000ms)
  4. Send *CLS (clear status)
  5. Send :FORM:ELEM READ (set reading format)
```

### Reading Current

`ReadCurrent(channel, out current)`:
1. Send `:READ?`
2. Parse comma-separated response
3. Pick element at `channel - 1` index (1-based channel numbering)

`ReadAllChannels(out currents[])`:
1. Send `:READ?`
2. Split on commas
3. Parse all values into array

### Voltage Source Control

| Method | SCPI Command |
|--------|-------------|
| `SetVoltage(volts)` | `:SOUR:VOLT {volts}` |
| `GetVoltage(out volts)` | `:SOUR:VOLT?` |
| `SetVoltageEnabled(true)` | `:SOUR:VOLT:STAT ON` |
| `SetVoltageEnabled(false)` | `:SOUR:VOLT:STAT OFF` |

### Configuration Modes

**Fast Mode** (~50-80 Hz measurement rate):
```
:SENS:CURR:NPLC 0.1           (0.1 power line cycles)
:SENS:CURR:AVER:STAT ON       (averaging enabled)
:SENS:CURR:AVER:COUN 3        (3 readings averaged)
:SYST:AZER:STAT ON            (auto-zero on)
```

**Default Mode** (~10 Hz, higher accuracy):
```
:SENS:CURR:NPLC 1.0           (1.0 power line cycle)
:SENS:CURR:AVER:STAT OFF      (averaging off)
```

### Reset

Sends `*RST`, waits 500ms, then sends `*CLS`.

### Thread Safety

All operations wrapped in `lock (_lock)`.

## CurrentMeasurementManager

Central manager with `ConcurrentDictionary<string, ICurrentMeasurementDevice>` (case-insensitive).

### Device Factory

Supports device type `"keithley_6482"` → creates `Keithley6482Adapter`.

### Polling

`StartPolling(intervalMs=500)` creates a `PeriodicTimer` that reads all channels from each connected device and fires `OnStatusUpdated(deviceName, CurrentMeasurementStatus)`.

`StopPolling()` cancels the timer and waits for the poll task.

### Events

`OnStatusUpdated(string deviceName, CurrentMeasurementStatus status)` — fired per device per poll cycle.

`CurrentMeasurementStatus` record:
```csharp
record CurrentMeasurementStatus(
    double[] ChannelReadings,
    string Unit = "A",
    bool Valid,
    DateTimeOffset Timestamp
);
```

### Disposal

Implements `IDisposable`: `Dispose()` → `StopPolling()` → `DisconnectAll()`.

## Config Format

```json
{
  "Devices": [
    {
      "Name": "Keithley-1",
      "Type": "keithley_6482",
      "VisaResource": "GPIB0::22::INSTR",
      "Enabled": true,
      "ChannelCount": 2,
      "EnabledChannels": [1, 2]
    }
  ]
}
```

## VisaNative P/Invoke

DLL: `visa64.dll` (NI-VISA runtime required)

| Function | Description |
|----------|-------------|
| `viOpenDefaultRM(out session)` | Open VISA resource manager |
| `viOpen(rm, resource, ..., out session)` | Open instrument session |
| `viClose(session)` | Close session |
| `viSetAttribute(session, attr, value)` | Set timeout, etc. |
| `viWrite(session, buf, len, out written)` | Send command |
| `viRead(session, buf, len, out read)` | Read response |
| `viStatusDesc(session, status, out desc)` | Error description |

Helper methods:
- `WriteString(vi, command)` — appends `\n`, calls `viWrite`
- `ReadString(vi, out response)` — reads up to 4096 bytes, trims `\r\n`

## Error Codes

```csharp
enum CurrentMeasurementError
{
    Success, DeviceNotFound, Disconnected, InvalidChannel,
    OperationFailed, CommunicationError, ParseError
}
```

## Thread Model

- `Keithley6482Adapter`: `lock` guards all VISA session access
- `CurrentMeasurementManager`: `ConcurrentDictionary` for registry, `PeriodicTimer` for async polling
- Polling reads execute sequentially per device within the timer callback
