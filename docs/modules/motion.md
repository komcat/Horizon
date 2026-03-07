# Horizon.Motion

Motion control subsystem supporting ACS SPiiPlus and PI hexapod controllers. Provides a unified interface for multi-axis absolute/relative movement, homing, named positions, and teach point overlays.

## Dependencies

- **Horizon.Common** (project reference)
- **ACS.SPiiPlusNET6.dll** (managed wrapper)
- **PI_GCS2_DLL_x64.dll** (native, copied to output)

## Files

| File | Description |
|------|-------------|
| `UnifiedMotionManager.cs` | Top-level orchestrator for all motion devices |
| `ACSMotionController.cs` | ACS SPiiPlus adapter |
| `PIMotionController.cs` | PI GCS2 hexapod adapter |
| `ACSControllerManager.cs` | Sub-manager for ACS controllers |
| `PIControllerManager.cs` | Sub-manager for PI controllers |
| `MotionJsonLoader.cs` | Config loader from `motor_devices.json` |
| `Interfaces/IMotionController.cs` | Hardware abstraction interface |
| `Interfaces/IMotionControllerManager.cs` | Manager-level interface |
| `Models/MotionTypes.cs` | PositionStruct, MotionDevice config, MotionError enum, MotionStatus |
| `Native/PIGcs2.cs` | P/Invoke wrapper for `PI_GCS2_DLL_x64.dll` |

## IMotionController

```csharp
public interface IMotionController
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }

    bool MoveToPosition(string axis, double position, bool blocking);
    bool MoveRelative(string axis, double distance, bool blocking);
    bool IsMoving(string axis);
    bool IsOnTarget(string axis);
    bool IsDeviceInMotion();
    PositionStruct GetCurrentPosition();

    bool HomeAxis(string axis);
    bool StopAxis(string axis);
    bool StopAll();

    string ControllerType { get; }  // "ACS" or "PI"
    string DeviceName { get; }
    List<string> AxisNames { get; }
}
```

## UnifiedMotionManager

Top-level orchestrator holding both `ACSControllerManager` and `PIControllerManager`. Routes operations to the correct sub-manager based on device type.

### Device Type Tracking

`_deviceTypes[deviceName]` maps each device to `"ACS"` or `"PI"`.

### Named Position System

Two position dictionaries per device:
- `_namedPositions[device]` — configured positions from JSON (immutable at runtime)
- `_teachPositions[device]` — runtime teach positions (override config)

`GetNamedPosition(device, name)` checks teach positions first, then config positions.

### Key Methods

| Method | Description |
|--------|-------------|
| `MoveToAbs(device, axis, position, blocking)` | Single-axis absolute move |
| `MoveToAbs6(device, PositionStruct, blocking)` | All 6 axes simultaneously |
| `MoveRelative(device, axis, distance, blocking)` | Single-axis relative move |
| `MoveToNamedPosition(device, name, blocking)` | Move to preset/teach position |
| `GetCurrentPosition(device)` | Returns `PositionStruct` |
| `IsMoving(device, axis)` | Check if axis is in motion |
| `StopAll(device)` | Emergency stop single device |
| `StopAllDevices()` | Emergency stop ALL controllers |
| `SetTeachPosition(device, name, pos)` | Store runtime position |
| `ClearTeachPositions()` | Clear all teach overlays |

### Events

- `PresetsChanged` — fires when teach positions are modified (UI refresh trigger)

### Disposal

Does NOT implement `IDisposable`. Cleanup via `DisconnectAll()` which disconnects all sub-managers.

## ACSMotionController

ACS SPiiPlus managed API (`ACS.SPiiPlusNET` namespace).

### Connection

TCP Ethernet: `_api.OpenCommEthernetTCP(ipAddress, port)` (default port 701)

### Axis Mapping

`Dictionary<string, Axis>` where `Axis` is ACS enum (ACSC_AXIS_0 through ACSC_AXIS_7). Populated from config `InstalledAxes[]`.

### Movement

- `MoveToPosition()`: `_api.ToPoint(0, acsAxis, position)` + optional `WaitForMotionComplete()` (30s timeout)
- `MoveRelative()`: Gets current position, calls `ToPoint()` with `current + distance`
- Wait loop: `Thread.Sleep(10ms)` spin with `IsMoving()` check

### Status Queries

- `IsMoving()`: checks `ACSC_MST_MOVE` motor state flag
- `IsOnTarget()`: checks `ACSC_MST_INPOS` motor state flag
- `GetCurrentPosition()`: calls `GetFPosition()` for each installed axis

### Homing

Not implemented — requires ACSPL+ program execution (returns `false`).

### Stopping

- `StopAxis()`: `_api.Halt(acsAxis)`
- `StopAll()`: `_api.HaltM(allAxes)`

## PIMotionController

PI GCS2 via P/Invoke wrapper.

### Connection

TCP/IP: `PIGcs2.ConnectTCPIP(ipAddress, port)` (default port 50000). Connection ID-based session.

### Axes

Hexapod standard: `{ "X", "Y", "Z", "U", "V", "W" }` or from device config `InstalledAxes[]`.

### Movement

- `MoveToPosition()`: `PIGcs2.MOV(connId, axisString, positionArray)`
- `MoveRelative()`: `PIGcs2.MVR(connId, axisString, distanceArray)`

### Status Queries

- `IsMoving()`: `PIGcs2.IsMoving()` returns 1/0
- `IsOnTarget()`: `PIGcs2.qONT()` queries on-target status
- `GetCurrentPosition()`: `PIGcs2.qPOS()` for each axis

### Homing

`PIGcs2.FRF(connId, axisString)` — find reference (home).

### Stopping

- `StopAll()`: `PIGcs2.STP(connId)` — all axes
- `StopAxis()`: `PIGcs2.HLT(connId, axisString)` — specific axis

## PositionStruct

```csharp
record PositionStruct
{
    double X, Y, Z, U, V, W;
    double[]? Waypoints;       // intermediate positions for multi-step moves
    double[]? ExitWaypoints;   // exit path positions
}
```

## Config Format

```json
{
  "Devices": [
    {
      "Id": "Hexapod-1",
      "Name": "Hexapod-1",
      "IpAddress": "192.168.0.10",
      "Port": 50000,
      "ControllerType": "PI",
      "InstalledAxes": ["X", "Y", "Z", "U", "V", "W"],
      "Enabled": true,
      "Positions": {
        "Home": { "X": 0, "Y": 0, "Z": 0, "U": 0, "V": 0, "W": 0 },
        "LoadPos": { "X": 10.0, "Y": 5.0, "Z": -2.0 }
      }
    }
  ]
}
```

## Native P/Invoke (PIGcs2.cs)

DLL: `PI_GCS2_DLL_x64.dll`

| Function | Description |
|----------|-------------|
| `PI_ConnectTCPIP(host, port)` | Connect, returns connection ID |
| `PI_CloseConnection(id)` | Disconnect |
| `PI_IsConnected(id)` | Check connection (0/1) |
| `PI_MOV(id, axes, positions[])` | Absolute move |
| `PI_MVR(id, axes, distances[])` | Relative move |
| `PI_qPOS(id, axes, positions[])` | Query positions |
| `PI_IsMoving(id, axes, flags[])` | Query moving state |
| `PI_qONT(id, axes, flags[])` | Query on-target |
| `PI_STP(id)` | Stop all axes |
| `PI_HLT(id, axes)` | Halt specific axis |
| `PI_FRF(id, axes)` | Find reference (home) |
| `PI_SVO(id, axes, states[])` | Set servo mode |
| `PI_GetError(id)` | Get last error code |
| `PI_TranslateError(code, buf, size)` | Error code to string |

## Thread Model

- `lock` guards per-controller hardware access
- Named position lookups are case-insensitive
- Motion wait loops use `Thread.Sleep(10ms)` spin (blocks calling thread)
- UI panels dispatch moves via `Task.Run()` to avoid blocking dispatcher
