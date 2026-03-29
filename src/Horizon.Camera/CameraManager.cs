using System.Collections.Concurrent;
using Basler.Pylon;
using Horizon.Camera.Interfaces;
using Horizon.Camera.Models;
using Serilog;

namespace Horizon.Camera;

public sealed class CameraManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ManagedCamera> _cameras = new();
    private readonly ConcurrentDictionary<string, SubscriberInfo> _subscribers = new();
    private readonly ConcurrentDictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CameraFrameData> _lastBroadcastFrames = new();
    private readonly ConcurrentDictionary<string, RegisteredCamera> _cameraConfigs = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _broadcastRunning;
    private CancellationTokenSource? _broadcastCts;
    private Task? _broadcastTask;
    private string _imageOutputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    private string _lastCapturedImagePath = string.Empty;
    private bool _disposed;

    // --- Camera Management ---

    public bool AddCamera(CameraInfo info)
    {
        var hw = CameraHardwareFactory.Create(info);
        var managed = new ManagedCamera(info, hw);
        var added = _cameras.TryAdd(info.Id, managed);
        if (added)
            Log.Information("[Camera] Camera added: Id={CameraId}, Type={CameraType}", info.Id, info.Type);
        return added;
    }

    public bool RemoveCamera(string cameraId)
    {
        if (!_cameras.TryRemove(cameraId, out var managed))
            return false;
        managed.Hardware.Dispose();
        Log.Information("[Camera] Camera removed: Id={CameraId}", cameraId);
        return true;
    }

    public ICameraHardware? GetCameraHardware(string cameraId)
    {
        return _cameras.TryGetValue(cameraId, out var managed) ? managed.Hardware : null;
    }

    public List<string> GetCameraIds() => _cameras.Keys.ToList();
    public int CameraCount => _cameras.Count;

    public void StoreCameraConfig(string cameraId, RegisteredCamera config)
    {
        _cameraConfigs[cameraId] = config;
    }

    public RegisteredCamera? GetCameraConfig(string cameraId)
    {
        return _cameraConfigs.TryGetValue(cameraId, out var config) ? config : null;
    }

    /// <summary>
    /// Raised when a camera's config is updated via UpdateCameraConfig.
    /// Passes the camera ID so listeners can react (e.g. click-to-move recalibration).
    /// </summary>
    public event Action<string>? CameraConfigChanged;

    /// <summary>
    /// Update a camera's calibration config in memory and notify listeners.
    /// Caller is responsible for persisting to disk (CameraConfigStore).
    /// </summary>
    public void UpdateCameraConfig(string cameraId, RegisteredCamera config)
    {
        _cameraConfigs[cameraId] = config;
        CameraConfigChanged?.Invoke(cameraId);
        Log.Information("[Camera] Config updated: Id={CameraId}", cameraId);
    }

    /// <summary>
    /// Get the friendly display name for a camera (e.g. "cam1").
    /// Returns the camera ID itself if no display name was registered.
    /// </summary>
    public string GetCameraDisplayName(string cameraId)
    {
        return _displayNames.TryGetValue(cameraId, out var name) ? name : cameraId;
    }

    /// <summary>
    /// Get all camera IDs with their display names.
    /// </summary>
    public List<(string Id, string DisplayName)> GetCamerasWithNames()
    {
        return _cameras.Keys
            .Select(id => (id, GetCameraDisplayName(id)))
            .ToList();
    }

    // --- Camera Operations ---

    public bool ConnectCamera(string cameraId)
    {
        if (!_cameras.TryGetValue(cameraId, out var managed)) return false;
        var result = managed.Hardware.Connect();
        if (result)
        {
            var defaults = new ExposureSettings();
            managed.Hardware.SetExposureSettings(defaults);
            Log.Information("[Camera] Connect OK: Id={CameraId} (defaults: gain={Gain}, fps={Fps})",
                cameraId, defaults.Gain, defaults.FrameRate);
        }
        else
            Log.Error("[Camera] Connect FAILED: Id={CameraId}", cameraId);
        return result;
    }

    public bool DisconnectCamera(string cameraId)
    {
        if (!_cameras.TryGetValue(cameraId, out var managed)) return false;
        var result = managed.Hardware.Disconnect();
        if (result)
            Log.Information("[Camera] Disconnect OK: Id={CameraId}", cameraId);
        else
            Log.Error("[Camera] Disconnect FAILED: Id={CameraId}", cameraId);
        return result;
    }

    public bool StartGrabbing(string cameraId)
    {
        if (!_cameras.TryGetValue(cameraId, out var managed)) return false;
        var result = managed.Hardware.StartGrabbing();
        Log.Information("[Camera] StartGrabbing {Result}: Id={CameraId}", result ? "OK" : "FAILED", cameraId);
        return result;
    }

    public bool StopGrabbing(string cameraId)
    {
        if (!_cameras.TryGetValue(cameraId, out var managed)) return false;
        var result = managed.Hardware.StopGrabbing();
        Log.Information("[Camera] StopGrabbing {Result}: Id={CameraId}", result ? "OK" : "FAILED", cameraId);
        return result;
    }

    public bool StartGrabbingWithBroadcast(string cameraId)
    {
        if (!_cameras.TryGetValue(cameraId, out var managed)) return false;

        // Wire up frame callback for broadcasting
        managed.Hardware.FrameCallback = BroadcastFrame;

        if (!managed.Hardware.IsConnected)
        {
            if (!managed.Hardware.Connect())
                return false;
        }

        if (!managed.Hardware.StartGrabbing())
            return false;

        if (!_broadcastRunning)
            StartBroadcastSystem();

        Log.Information("[Camera] StartGrabbingWithBroadcast OK: Id={CameraId}", cameraId);
        return true;
    }

    // --- Exposure Control ---

    public bool ApplyExposureSettings(string cameraId, ExposureSettings settings)
    {
        if (!_cameras.TryGetValue(cameraId, out var managed)) return false;
        return managed.Hardware.SetExposureSettings(settings);
    }

    public ExposureSettings GetExposureSettings(string cameraId)
    {
        if (!_cameras.TryGetValue(cameraId, out var managed)) return new ExposureSettings();
        return managed.Hardware.GetExposureSettings();
    }

    // --- Camera Status ---

    public CameraStatus GetCameraStatus(string cameraId)
    {
        if (!_cameras.TryGetValue(cameraId, out var managed))
            return new CameraStatus { Id = cameraId };

        var hw = managed.Hardware;
        var config = GetCameraConfig(cameraId);

        // Get live image size from latest broadcast frame
        int liveW = 0, liveH = 0;
        if (_lastBroadcastFrames.TryGetValue(cameraId, out var lastFrame) && lastFrame.IsValid)
        {
            liveW = lastFrame.Width;
            liveH = lastFrame.Height;
        }

        return new CameraStatus
        {
            Id = cameraId,
            Type = hw.CameraType,
            Connected = hw.IsConnected,
            Grabbing = hw.IsGrabbing,
            DeviceRemoved = hw.IsDeviceRemoved,
            ModelName = hw.ModelName,
            SerialNumber = hw.SerialNumber,
            DeviceInfo = managed.Info.DeviceInfo,
            ExposureSettings = hw.IsConnected ? hw.GetExposureSettings() : new ExposureSettings(),
            ConfiguredWidth = config?.Width ?? 0,
            ConfiguredHeight = config?.Height ?? 0,
            MmPerPixel = config?.MmPerPixel ?? 0,
            Magnification = config?.Magnification ?? 1.0,
            LiveImageWidth = liveW,
            LiveImageHeight = liveH
        };
    }

    public List<CameraStatus> GetAllCameraStatus()
    {
        return _cameras.Keys.Select(GetCameraStatus).ToList();
    }

    // --- Broadcasting System ---

    public void StartBroadcastSystem()
    {
        if (_broadcastRunning) return;
        _broadcastRunning = true;
        _broadcastCts = new CancellationTokenSource();
        // Broadcast system is event-driven via FrameCallback, no polling thread needed
        Log.Information("[Camera] Broadcast system started");
    }

    public void StopBroadcastSystem()
    {
        _broadcastRunning = false;
        _broadcastCts?.Cancel();
        _broadcastCts?.Dispose();
        _broadcastCts = null;
        Log.Information("[Camera] Broadcast system stopped");
    }

    public void SubscribeToFrames(ICameraFrameSubscriber subscriber)
    {
        _subscribers[subscriber.SubscriberId] = new SubscriberInfo(subscriber);
        Log.Information("[Camera] Subscriber added: Id={SubscriberId}", subscriber.SubscriberId);
    }

    public void UnsubscribeFromFrames(string subscriberId)
    {
        _subscribers.TryRemove(subscriberId, out _);
        Log.Information("[Camera] Subscriber removed: Id={SubscriberId}", subscriberId);
    }

    public int SubscriberCount => _subscribers.Count;

    // --- Camera Discovery ---

    public static List<CameraInfo> DiscoverAllCameras()
    {
        var cameras = new List<CameraInfo>();
        cameras.AddRange(DiscoverPylonCameras());
        cameras.AddRange(DiscoverIDSCameras());
        return cameras;
    }

    public static List<CameraInfo> DiscoverPylonCameras()
    {
        var cameras = new List<CameraInfo>();
        try
        {
            var devices = CameraFinder.Enumerate();
            foreach (var device in devices)
            {
                var serial = device[CameraInfoKey.SerialNumber];
                cameras.Add(new CameraInfo
                {
                    Id = serial,
                    SerialNumber = serial,
                    ModelName = device[CameraInfoKey.ModelName],
                    VendorName = device[CameraInfoKey.VendorName],
                    IpAddress = device.ContainsKey(CameraInfoKey.DeviceIpAddress) ? device[CameraInfoKey.DeviceIpAddress] : string.Empty,
                    ConnectionMethod = ConnectionMethod.SerialNumber,
                    Type = CameraType.Pylon,
                    DeviceInfo = $"Basler {device[CameraInfoKey.ModelName]} ({serial})"
                });
            }
        }
        catch { /* Pylon SDK may not be available */ }
        return cameras;
    }

    public static List<CameraInfo> DiscoverIDSCameras()
    {
        // Use legacy uEye SDK (P/Invoke) — works with all IDS cameras including older models
        return Adapters.UEyeCameraAdapter.DiscoverCameras();
    }

    public bool DiscoverAndAddAllCameras()
    {
        var cameras = DiscoverAllCameras();
        foreach (var info in cameras)
            AddCamera(info);
        Log.Information("[Camera] DiscoverAndAddAllCameras: {Count} camera(s) discovered", cameras.Count);
        return cameras.Count > 0;
    }

    public bool DiscoverAndAddRegisteredCameras(List<Models.RegisteredCamera> registered)
    {
        if (registered == null || registered.Count == 0)
            return false;

        int added = 0;

        // IP cameras — create CameraInfo directly, no discovery needed
        var ipCameras = registered.Where(r =>
            r.ConnectionType == "IP" && !string.IsNullOrWhiteSpace(r.IpAddress)).ToList();

        foreach (var reg in ipCameras)
        {
            var info = new CameraInfo
            {
                Id = reg.SerialNumber,
                SerialNumber = reg.SerialNumber,
                IpAddress = reg.IpAddress,
                ConnectionMethod = ConnectionMethod.IPAddress,
                Type = reg.SdkType,
                ModelName = reg.Name,
                VendorName = reg.SdkType == CameraType.Pylon ? "Basler" : "IDS",
                DeviceInfo = $"{reg.Name} ({reg.SerialNumber}) [{reg.IpAddress}]"
            };
            if (AddCamera(info))
            {
                if (!string.IsNullOrEmpty(reg.Name))
                    _displayNames[reg.SerialNumber] = reg.Name;
                StoreCameraConfig(reg.SerialNumber, reg);
                added++;
            }
        }

        // USB cameras — discover hardware and filter by serial
        var usbCameras = registered.Where(r => r.ConnectionType != "IP").ToList();
        if (usbCameras.Count > 0)
        {
            var allCameras = DiscoverAllCameras();
            var usbSerials = new HashSet<string>(
                usbCameras.Select(r => r.SerialNumber),
                StringComparer.OrdinalIgnoreCase);

            foreach (var info in allCameras.Where(c => usbSerials.Contains(c.SerialNumber)))
            {
                if (AddCamera(info))
                {
                    var reg = usbCameras.FirstOrDefault(r =>
                        string.Equals(r.SerialNumber, info.SerialNumber, StringComparison.OrdinalIgnoreCase));
                    if (reg != null)
                    {
                        if (!string.IsNullOrEmpty(reg.Name))
                            _displayNames[info.SerialNumber] = reg.Name;
                        StoreCameraConfig(info.SerialNumber, reg);
                    }
                    added++;
                }
            }
        }

        Log.Information("[Camera] DiscoverAndAddRegisteredCameras: {Count} camera(s) added", added);
        return added > 0;
    }

    // --- Image Capture ---

    public bool CaptureImage(string cameraId)
    {
        return CaptureImage(cameraId, GenerateImageFilename(cameraId, ".bmp"));
    }

    public bool CaptureImage(string cameraId, string filename)
    {
        if (!_cameras.TryGetValue(cameraId, out var managed)) return false;

        var frame = new CameraFrameData();
        bool grabbed = managed.Hardware.CaptureFrame(frame);

        // If direct capture failed (camera busy with continuous grabbing),
        // use the latest broadcast frame instead
        if (!grabbed)
        {
            if (_lastBroadcastFrames.TryGetValue(cameraId, out var cached) &&
                cached.ImageData != null && cached.ImageData.Length > 0)
                frame = cached;
            else
                return false;
        }

        var filePath = Path.Combine(_imageOutputDirectory, filename);
        if (SaveFrameToBmp(frame, filePath))
        {
            _lastCapturedImagePath = filePath;
            Log.Information("[Camera] Capture OK: Id={CameraId}, Path={FilePath}", cameraId, filePath);
            return true;
        }
        Log.Error("[Camera] Capture FAILED: Id={CameraId}, Path={FilePath}", cameraId, filePath);
        return false;
    }

    public void SetImageOutputDirectory(string directory) => _imageOutputDirectory = directory;
    public string GetImageOutputDirectory() => _imageOutputDirectory;
    public string GetLastCapturedImagePath() => _lastCapturedImagePath;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopBroadcastSystem();
        foreach (var kvp in _cameras)
        {
            try { kvp.Value.Hardware.Dispose(); } catch { }
        }
        _cameras.Clear();
        _subscribers.Clear();
    }

    // --- Private helpers ---

    private void BroadcastFrame(CameraFrameData frame)
    {
        if (!_broadcastRunning) return;

        // Cache latest frame for snapshot capture while grabbing
        _lastBroadcastFrames[frame.CameraId] = frame;

        foreach (var kvp in _subscribers)
        {
            var sub = kvp.Value;
            if (!sub.Subscriber.WantsFramesFromCamera(frame.CameraId))
                continue;

            // Rate limiting
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (sub.Subscriber.MinFrameIntervalMs > 0 &&
                (now - sub.LastBroadcastMs) < sub.Subscriber.MinFrameIntervalMs)
                continue;

            sub.LastBroadcastMs = now;
            try { sub.Subscriber.OnNewFrame(frame); } catch { }
        }
    }

    private static bool SaveFrameToBmp(CameraFrameData frame, string filePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            int rowSize = ((frame.Width * 3 + 3) / 4) * 4; // BMP rows padded to 4 bytes
            int imageSize = rowSize * frame.Height;
            int fileSize = 54 + imageSize;

            // BMP file header
            bw.Write((ushort)0x4D42); // 'BM'
            bw.Write(fileSize);
            bw.Write(0); // reserved
            bw.Write(54); // pixel data offset

            // DIB header (BITMAPINFOHEADER)
            bw.Write(40); // header size
            bw.Write(frame.Width);
            bw.Write(frame.Height);
            bw.Write((ushort)1); // planes
            bw.Write((ushort)24); // bits per pixel
            bw.Write(0); // compression
            bw.Write(imageSize);
            bw.Write(2835); // X pixels per meter (~72 DPI)
            bw.Write(2835); // Y pixels per meter
            bw.Write(0); // colors used
            bw.Write(0); // important colors

            // Pixel data: BMP is bottom-up, BGR format
            int padding = rowSize - frame.Width * 3;
            var padBytes = new byte[padding];

            for (int y = frame.Height - 1; y >= 0; y--)
            {
                int srcOffset = y * frame.Width * frame.Channels;
                for (int x = 0; x < frame.Width; x++)
                {
                    int idx = srcOffset + x * frame.Channels;
                    bw.Write(frame.ImageData[idx + 2]); // B
                    bw.Write(frame.ImageData[idx + 1]); // G
                    bw.Write(frame.ImageData[idx + 0]); // R
                }
                if (padding > 0)
                    bw.Write(padBytes);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GenerateImageFilename(string cameraId, string extension)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var safeCameraId = string.Join("_", cameraId.Split(Path.GetInvalidFileNameChars()));
        return $"{safeCameraId}_{timestamp}{extension}";
    }

    private sealed class ManagedCamera
    {
        public CameraInfo Info { get; }
        public ICameraHardware Hardware { get; }

        public ManagedCamera(CameraInfo info, ICameraHardware hardware)
        {
            Info = info;
            Hardware = hardware;
        }
    }

    private sealed class SubscriberInfo
    {
        public ICameraFrameSubscriber Subscriber { get; }
        public long LastBroadcastMs { get; set; }

        public SubscriberInfo(ICameraFrameSubscriber subscriber)
        {
            Subscriber = subscriber;
        }
    }
}
