# Horizon.Camera

Camera subsystem supporting three SDK families: Basler Pylon, IDS Peak (GenICam), and IDS uEye (legacy). Provides discovery, connection, live streaming, frame broadcasting, and image capture.

## Dependencies

- **Horizon.Common** (project reference)
- **Basler.Pylon** (managed DLL from Pylon 8 SDK)
- **ids_peak_dotnet**, **ids_peak_ipl_dotnet**, **ids_peak_afl_dotnet** (managed DLLs)
- **ueye_api_64.dll** (P/Invoke)

## Files

| File | Description |
|------|-------------|
| `CameraManager.cs` | Central orchestrator for all cameras |
| `CameraHardwareFactory.cs` | Static factory: `CameraInfo` → adapter instance |
| `CameraConfigStore.cs` | JSON persistence for registered cameras |
| `LiveVideoSubscriber.cs` | Thread-safe frame subscriber for UI panels |
| `Interfaces/ICameraHardware.cs` | Hardware abstraction interface |
| `Interfaces/ICameraFrameSubscriber.cs` | Frame broadcast observer interface |
| `Adapters/PylonCameraAdapter.cs` | Basler Pylon SDK adapter |
| `Adapters/IDSPeakCameraAdapter.cs` | IDS Peak GenICam adapter |
| `Adapters/UEyeCameraAdapter.cs` | Legacy IDS uEye adapter |
| `Native/UEyeNative.cs` | P/Invoke declarations for `ueye_api_64.dll` |
| `Models/*.cs` | 7 model files (CameraFrameData, CameraInfo, CameraStatus, ExposureSettings, RegisteredCamera, enums) |

## ICameraHardware

```csharp
public interface ICameraHardware : IDisposable
{
    bool Initialize();
    bool Connect();
    bool Disconnect();
    bool StartGrabbing();
    bool StopGrabbing();

    bool IsConnected { get; }
    bool IsGrabbing { get; }
    bool IsDeviceRemoved { get; }
    bool HasNewFrame { get; }

    bool CaptureFrame(CameraFrameData frameData);
    Action<CameraFrameData>? FrameCallback { get; set; }

    string CameraId { get; }
    string ModelName { get; }
    string SerialNumber { get; }
    CameraType CameraType { get; }

    bool SetExposureSettings(ExposureSettings settings);
    ExposureSettings GetExposureSettings();

    // Optional key-value configuration (default implementations)
    bool SetConfiguration(string key, string value) => false;
    string GetConfiguration(string key) => string.Empty;
}
```

## CameraManager

Sealed class managing the full camera lifecycle. Uses `ConcurrentDictionary<string, ManagedCamera>` for thread-safe camera storage.

### Key Operations

| Method | Description |
|--------|-------------|
| `AddCamera(CameraInfo)` | Register and create hardware adapter via factory |
| `RemoveCamera(id)` | Unregister and dispose hardware |
| `ConnectCamera(id)` | Initialize + connect hardware |
| `DisconnectCamera(id)` | Stop grabbing + disconnect |
| `StartGrabbing(id)` | Begin frame acquisition |
| `StopGrabbing(id)` | Stop acquisition |
| `ApplyExposureSettings(id, settings)` | Apply exposure/gain/auto settings |
| `CaptureImage(id)` | Capture single frame, encode as BMP, save to disk |

### Discovery

Static methods scan for available cameras:
- `DiscoverPylonCameras()` — uses `CameraFinder.Enumerate()` from Pylon SDK
- `DiscoverIDSCameras()` — uses `peak.DeviceManager` enumeration
- `DiscoverAndAddRegisteredCameras(list)` — matches discovered cameras against registered config

### Frame Broadcasting

A background broadcast system distributes frames to subscribers:

```
CameraManager.StartBroadcastSystem()
  → ICameraHardware.FrameCallback fires on SDK thread
  → ConcurrentDictionary<string, SubscriberInfo> distributes to subscribers
  → Each subscriber has MinFrameIntervalMs throttle
```

- `SubscribeToFrames(subscriber)` / `UnsubscribeFromFrames(subscriberId)`
- Subscribers implement `ICameraFrameSubscriber` with `OnNewFrame()` callback

### Image Capture

`CaptureImage(id)` captures a single frame and saves as 24-bit BMP:
- Bottom-up RGB pixel layout
- Row padding to 4-byte boundary
- Output directory configurable via `SetImageOutputDirectory()`

### Disposal

`Dispose()` stops the broadcast system, disposes all `ICameraHardware` instances, clears collections.

## Adapters

### PylonCameraAdapter (Basler Pylon SDK)

- Connection via SerialNumber, IPAddress, DeviceIndex, or Auto
- Event-driven frame capture: `ImageGrabbedEventArgs` → `PixelDataConverter` → RGB8packed
- Dual exposure parameter support: ACE (`ExposureTime`) vs ACE2 (`ExposureTimeAbs`)
- Frame rate: `AcquisitionFrameRate` vs `AcquisitionFrameRateAbs`
- Generic config: `SetConfiguration(key, value)` via `Parameters[key].ParseAndSetValue`
- IP resolution fallback chain

### IDSPeakCameraAdapter (IDS Peak GenICam)

- `peak.DeviceManager` enumeration with optional legacy GenTL producer (.cti)
- `DataStream` buffer allocation and cycling
- Image conversion via `ImageConverter` to RGB8
- Background grab task: 1000ms `WaitForFinishedBuffer` timeout
- GenICam node access for ExposureTime, Gain, auto modes

### UEyeCameraAdapter (Legacy IDS uEye P/Invoke)

- `ueye_api_64.dll` P/Invoke via `UEyeNative.cs`
- Camera handle lifecycle: `InitCamera` → `SetColorMode(RGB8)` → `AllocImageMem` → `SetImageMem`
- `CaptureVideo` for live mode, background poll task
- Exposure in milliseconds (converted to/from microseconds for interface)
- Gain: 0-100 uEye scale mapped from settings

## Models

| Model | Description |
|-------|-------------|
| `CameraFrameData` | Frame buffer with ImageData byte[], Width, Height, Channels, metadata |
| `CameraInfo` | Discovery metadata: Id, SerialNumber, IPAddress, ConnectionMethod, CameraType |
| `CameraStatus` | Live state snapshot: Connected, Grabbing, ExposureSettings |
| `ExposureSettings` | Immutable record: ExposureTimeUs, Gain, AutoExposure, AutoGain, FrameRate |
| `RegisteredCamera` | Serialization model: Name, SerialNumber, SdkType, ConnectionType, IpAddress |

## CameraConfigStore

Persistent store at `%APPDATA%\Horizon\config\camera-config.json`:
- `Load()` returns `List<RegisteredCamera>`
- `Save(cameras)` writes prettified JSON
- Migrates from legacy path `%APPDATA%\Horizon\camera-config.json` on first load

## Thread Model

- `ConcurrentDictionary` for camera registry and subscribers
- Frame callbacks fire on SDK threads (Pylon event thread, IDS grab task, uEye poll task)
- `LiveVideoSubscriber` uses `lock` for thread-safe frame buffer access
- UI panels must marshal frame data to dispatcher thread
