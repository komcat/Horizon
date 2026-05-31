using System.Runtime.InteropServices;
using Horizon.Camera.Interfaces;
using Horizon.Camera.Models;
using static Horizon.Camera.Adapters.UEyeNative;

namespace Horizon.Camera.Adapters;

/// <summary>
/// Camera adapter for legacy IDS uEye cameras using direct P/Invoke to ueye_api_64.dll.
/// Mirrors bobcat_engine's IDSCameraAdapter.cpp logic exactly.
/// </summary>
public sealed class UEyeCameraAdapter : ICameraHardware
{
    private readonly CameraInfo _info;
    private readonly int _deviceId;
    private uint _hCam;
    private IntPtr _imageMemory;
    private int _memoryId;
    private volatile bool _isConnected;
    private volatile bool _isGrabbing;
    private volatile bool _isDeviceRemoved;
    private string _lastError = string.Empty;
    private string _modelName = string.Empty;
    private string _serialNumber = string.Empty;
    private CancellationTokenSource? _grabCts;
    private Task? _grabTask;
    private int _imageWidth;
    private int _imageHeight;
    private bool _disposed;

    // Reuse a small ring of frame objects for the live grab loop instead of
    // allocating a new W*H*3 buffer every frame (which churned the GC at video
    // rates). Each slot keeps its own ImageData, which CaptureFrame only reallocs
    // when the size grows; the ring gives async UI readers a few frames of slack.
    // GrabLoop is the only writer, so the index needs no lock.
    private const int FrameRingSize = 3;
    private CameraFrameData[]? _liveFrames;
    private int _liveFrameIndex;

    public UEyeCameraAdapter(CameraInfo info, int deviceId)
    {
        _info = info;
        _deviceId = deviceId;
    }

    public bool IsConnected => _isConnected;
    public bool IsGrabbing => _isGrabbing;
    public bool IsDeviceRemoved => _isDeviceRemoved;
    public bool HasNewFrame => _isGrabbing; // In live mode, always has frames
    public string CameraId => _info.Id;
    public string ModelName => _modelName;
    public string SerialNumber => _serialNumber;
    public string VendorName => "IDS Imaging";
    public CameraType CameraType => CameraType.IDS;
    public string LastError => _lastError;
    public Action<CameraFrameData>? FrameCallback { get; set; }

    public bool Initialize() => true;

    public bool Connect()
    {
        try
        {
            if (_isConnected) return true;

            _hCam = (uint)_deviceId;
            int result = UEyeNative.InitCamera(ref _hCam, IntPtr.Zero);
            if (result != IS_SUCCESS)
            {
                SetError($"InitCamera failed (device {_deviceId}): error {result}");
                return false;
            }

            _isConnected = true;
            _isDeviceRemoved = false;

            UpdateCameraProperties();
            InitializeImageMemory();

            return true;
        }
        catch (Exception ex)
        {
            SetError($"Connect failed: {ex.Message}");
            return false;
        }
    }

    public bool Disconnect()
    {
        try
        {
            if (!_isConnected) return true;

            StopGrabbing();
            CleanupImageMemory();

            if (_hCam != 0)
            {
                UEyeNative.ExitCamera(_hCam);
                _hCam = 0;
            }

            _isConnected = false;
            return true;
        }
        catch (Exception ex)
        {
            SetError($"Disconnect failed: {ex.Message}");
            return false;
        }
    }

    public bool StartGrabbing()
    {
        try
        {
            if (!_isConnected) return false;
            if (_isGrabbing) return true;

            int result = UEyeNative.CaptureVideo(_hCam, IS_DONT_WAIT);
            if (result != IS_SUCCESS)
            {
                SetError($"CaptureVideo failed: error {result}");
                return false;
            }

            _isGrabbing = true;

            // Start background grab thread (mirrors C++ GrabThreadFunction)
            _grabCts = new CancellationTokenSource();
            var token = _grabCts.Token;
            _grabTask = Task.Run(() => GrabLoop(token), token);

            return true;
        }
        catch (Exception ex)
        {
            SetError($"StartGrabbing failed: {ex.Message}");
            return false;
        }
    }

    public bool StopGrabbing()
    {
        try
        {
            if (!_isGrabbing) return true;

            _isGrabbing = false;
            _grabCts?.Cancel();
            try { _grabTask?.Wait(2000); } catch { }
            _grabCts?.Dispose();
            _grabCts = null;
            _grabTask = null;

            if (_hCam != 0)
                UEyeNative.StopLiveVideo(_hCam, IS_FORCE_VIDEO_STOP);

            return true;
        }
        catch (Exception ex)
        {
            SetError($"StopGrabbing failed: {ex.Message}");
            return false;
        }
    }

    public bool CaptureFrame(CameraFrameData frameData)
    {
        try
        {
            if (!_isConnected || _imageMemory == IntPtr.Zero) return false;

            // Get current AOI dimensions
            var rect = new IS_RECT();
            if (UEyeNative.AOI(_hCam, IS_AOI_IMAGE_GET_AOI, ref rect, Marshal.SizeOf<IS_RECT>()) != IS_SUCCESS)
                return false;

            int width = rect.s32Width;
            int height = rect.s32Height;

            // Check video is running
            if (_isGrabbing)
            {
                int videoStarted = 0;
                if (UEyeNative.HasVideoStarted(_hCam, ref videoStarted) != IS_SUCCESS || videoStarted == 0)
                    return false;
            }

            frameData.CameraId = _info.Id;
            frameData.Width = width;
            frameData.Height = height;
            frameData.Channels = 3;
            frameData.Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            int imageSize = width * height * 3;
            if (frameData.ImageData.Length < imageSize)
                frameData.ImageData = new byte[imageSize];

            Marshal.Copy(_imageMemory, frameData.ImageData, 0, imageSize);
            return true;
        }
        catch (Exception ex)
        {
            SetError($"CaptureFrame failed: {ex.Message}");
            return false;
        }
    }

    public bool SetExposureSettings(ExposureSettings settings)
    {
        if (!_isConnected) return false;

        bool success = true;

        // Auto exposure
        double autoShutter = settings.AutoExposure ? 1.0 : 0.0;
        double dummy = 0.0;
        UEyeNative.SetAutoParameter(_hCam, IS_SET_ENABLE_AUTO_SHUTTER, ref autoShutter, ref dummy);

        // Exposure time (if not auto) — uEye uses milliseconds
        if (!settings.AutoExposure)
        {
            double exposureMs = settings.ExposureTimeUs / 1000.0;
            if (UEyeNative.Exposure(_hCam, IS_EXPOSURE_CMD_SET_EXPOSURE, ref exposureMs, sizeof(double)) != IS_SUCCESS)
                success = false;
        }

        // Auto gain
        double autoGain = settings.AutoGain ? 1.0 : 0.0;
        UEyeNative.SetAutoParameter(_hCam, IS_SET_ENABLE_AUTO_GAIN, ref autoGain, ref dummy);

        // Gain (if not auto) — uEye uses 0-100 scale
        if (!settings.AutoGain)
        {
            int idsGain = Math.Clamp((int)(settings.Gain * 10), 0, 100);
            UEyeNative.SetHardwareGain(_hCam, idsGain, IS_IGNORE_PARAMETER, IS_IGNORE_PARAMETER, IS_IGNORE_PARAMETER);
        }

        return success;
    }

    public ExposureSettings GetExposureSettings()
    {
        if (!_isConnected) return new ExposureSettings();

        double exposureMs = 0;
        UEyeNative.Exposure(_hCam, IS_EXPOSURE_CMD_GET_EXPOSURE, ref exposureMs, sizeof(double));

        int gain = UEyeNative.SetHardwareGain(_hCam, IS_GET_MASTER_GAIN, IS_IGNORE_PARAMETER, IS_IGNORE_PARAMETER, IS_IGNORE_PARAMETER);

        double autoShutter = 0, autoGain = 0, dummy = 0;
        UEyeNative.SetAutoParameter(_hCam, IS_GET_ENABLE_AUTO_SHUTTER, ref autoShutter, ref dummy);
        UEyeNative.SetAutoParameter(_hCam, IS_GET_ENABLE_AUTO_GAIN, ref autoGain, ref dummy);

        return new ExposureSettings
        {
            ExposureTimeUs = exposureMs * 1000.0,
            Gain = gain >= 0 ? gain / 10.0 : 0,
            AutoExposure = autoShutter > 0,
            AutoGain = autoGain > 0
        };
    }

    public void ClearLastError() => _lastError = string.Empty;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    // --- Discovery (static) ---

    public static List<CameraInfo> DiscoverCameras()
    {
        var cameras = new List<CameraInfo>();
        try
        {
            int numCameras = 0;
            if (UEyeNative.GetNumberOfCameras(ref numCameras) != IS_SUCCESS || numCameras <= 0)
                return cameras;

            // Allocate UEYE_CAMERA_LIST: dwCount (4 bytes) + N * UEYE_CAMERA_INFO
            int infoSize = Marshal.SizeOf<UEYE_CAMERA_INFO>();
            int totalSize = 4 + numCameras * infoSize;
            IntPtr ptr = Marshal.AllocHGlobal(totalSize);

            try
            {
                // Set dwCount
                Marshal.WriteInt32(ptr, numCameras);

                if (UEyeNative.GetCameraList(ptr) == IS_SUCCESS)
                {
                    for (int i = 0; i < numCameras; i++)
                    {
                        var info = Marshal.PtrToStructure<UEYE_CAMERA_INFO>(ptr + 4 + i * infoSize);
                        cameras.Add(new CameraInfo
                        {
                            Id = $"IDS_Camera_{info.dwCameraID}",
                            SerialNumber = info.SerNo?.Trim() ?? "",
                            ModelName = info.FullModelName?.Trim() ?? info.Model?.Trim() ?? "IDS Camera",
                            VendorName = "IDS Imaging",
                            ConnectionMethod = ConnectionMethod.DeviceIndex,
                            Type = CameraType.IDS,
                            DeviceIndex = (int)info.dwDeviceID,
                            DeviceInfo = $"IDS {info.FullModelName?.Trim()} (ID:{info.dwCameraID}, Serial:{info.SerNo?.Trim()})"
                        });
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch { /* uEye SDK not available */ }
        return cameras;
    }

    // --- Private helpers ---

    private void UpdateCameraProperties()
    {
        var camInfo = new CAMINFO();
        if (UEyeNative.GetCameraInfo(_hCam, ref camInfo) == IS_SUCCESS)
        {
            _modelName = camInfo.ID?.Trim() ?? "IDS Camera";
            _serialNumber = camInfo.SerNo?.Trim() ?? "";
        }
    }

    private void InitializeImageMemory()
    {
        var rect = new IS_RECT();
        if (UEyeNative.AOI(_hCam, IS_AOI_IMAGE_GET_AOI, ref rect, Marshal.SizeOf<IS_RECT>()) != IS_SUCCESS)
            return;

        _imageWidth = rect.s32Width;
        _imageHeight = rect.s32Height;

        // Set RGB8 color mode (matches C++ code)
        int result = UEyeNative.SetColorMode(_hCam, IS_CM_RGB8_PACKED);
        if (result != IS_SUCCESS)
        {
            // Fallback to BGR8
            UEyeNative.SetColorMode(_hCam, IS_CM_BGR8_PACKED);
        }

        int bitsPerPixel = 24;
        IntPtr imgMem = IntPtr.Zero;
        int memId = 0;

        result = UEyeNative.AllocImageMem(_hCam, _imageWidth, _imageHeight, bitsPerPixel, ref imgMem, ref memId);
        if (result != IS_SUCCESS) return;

        result = UEyeNative.SetImageMem(_hCam, imgMem, memId);
        if (result != IS_SUCCESS)
        {
            UEyeNative.FreeImageMem(_hCam, imgMem, memId);
            return;
        }

        _imageMemory = imgMem;
        _memoryId = memId;
    }

    private void CleanupImageMemory()
    {
        if (_hCam != 0 && _imageMemory != IntPtr.Zero)
        {
            UEyeNative.FreeImageMem(_hCam, _imageMemory, _memoryId);
            _imageMemory = IntPtr.Zero;
            _memoryId = 0;
        }
    }

    /// <summary>Next reusable live-frame slot (round-robin), lazily allocating the ring once.</summary>
    private CameraFrameData NextLiveFrame()
    {
        _liveFrames ??= new CameraFrameData[FrameRingSize];
        var frame = _liveFrames[_liveFrameIndex] ??= new CameraFrameData();
        _liveFrameIndex = (_liveFrameIndex + 1) % FrameRingSize;
        return frame;
    }

    private void GrabLoop(CancellationToken token)
    {
        var frameDuration = TimeSpan.FromMilliseconds(33); // ~30fps

        while (!token.IsCancellationRequested && _isGrabbing)
        {
            var frameStart = DateTime.UtcNow;
            try
            {
                int videoStarted = 0;
                int result = UEyeNative.HasVideoStarted(_hCam, ref videoStarted);

                if (result == IS_SUCCESS && videoStarted != 0)
                {
                    var callback = FrameCallback;
                    if (callback != null)
                    {
                        var frame = NextLiveFrame();
                        if (CaptureFrame(frame))
                            callback(frame);
                    }
                }
            }
            catch (Exception) when (token.IsCancellationRequested) { break; }
            catch { }

            // Frame rate control
            var elapsed = DateTime.UtcNow - frameStart;
            if (elapsed < frameDuration)
                Thread.Sleep(frameDuration - elapsed);
        }
    }

    private void SetError(string error) => _lastError = error;
}
