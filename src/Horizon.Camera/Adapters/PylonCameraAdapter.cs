using Basler.Pylon;
using Horizon.Camera.Interfaces;
using Horizon.Camera.Models;

namespace Horizon.Camera.Adapters;

public sealed class PylonCameraAdapter : ICameraHardware
{
    private Basler.Pylon.Camera? _camera;
    private readonly CameraInfo _info;
    private volatile bool _isConnected;
    private volatile bool _isGrabbing;
    private volatile bool _isDeviceRemoved;
    private volatile bool _hasNewFrame;
    private string _lastError = string.Empty;
    private PixelDataConverter? _converter;
    private bool _disposed;

    public PylonCameraAdapter(CameraInfo info)
    {
        _info = info;
    }

    public bool IsConnected => _isConnected;
    public bool IsGrabbing => _isGrabbing;
    public bool IsDeviceRemoved => _isDeviceRemoved;
    public bool HasNewFrame => _hasNewFrame;
    public string CameraId => _info.Id;
    public string ModelName => _camera?.CameraInfo[CameraInfoKey.ModelName] ?? _info.ModelName;
    public string SerialNumber => _camera?.CameraInfo[CameraInfoKey.SerialNumber] ?? _info.SerialNumber;
    public string VendorName => _camera?.CameraInfo[CameraInfoKey.VendorName] ?? "Basler";
    public CameraType CameraType => CameraType.Pylon;
    public string LastError => _lastError;
    public Action<CameraFrameData>? FrameCallback { get; set; }

    public bool Initialize()
    {
        try
        {
            _camera = CreateCamera();
            return _camera != null;
        }
        catch (Exception ex)
        {
            SetError($"Initialize failed: {ex.Message}");
            return false;
        }
    }

    public bool Connect()
    {
        try
        {
            if (_camera == null && !Initialize())
                return false;

            _camera!.Open();
            _isConnected = true;
            _isDeviceRemoved = false;

            // Create pixel converter after successful connection
            if (_converter == null)
            {
                _converter = new PixelDataConverter();
                _converter.OutputPixelFormat = PixelType.RGB8packed;
            }

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
            StopGrabbing();
            _camera?.Close();
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
            if (_camera == null || !_isConnected)
                return false;

            _camera.StreamGrabber.ImageGrabbed += OnImageGrabbed;
            _camera.StreamGrabber.Start(GrabStrategy.LatestImages, GrabLoop.ProvidedByStreamGrabber);
            _isGrabbing = true;
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
            if (_camera == null || !_isGrabbing)
                return true;

            _camera.StreamGrabber.Stop();
            _camera.StreamGrabber.ImageGrabbed -= OnImageGrabbed;
            _isGrabbing = false;
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
            if (_camera == null || !_isConnected)
                return false;

            using var grabResult = _camera.StreamGrabber.GrabOne(5000, TimeoutHandling.ThrowException);
            if (!grabResult.GrabSucceeded)
                return false;

            FillFrameData(grabResult, frameData);
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
        try
        {
            if (_camera == null || !_isConnected)
                return false;

            // Handle ACE vs ACE2 parameter naming
            SetExposureTime(settings.ExposureTimeUs);
            SetGain(settings.Gain);
            SetAutoExposure(settings.AutoExposure);
            SetAutoGain(settings.AutoGain);

            if (settings.FrameRate > 0)
                SetFrameRate(settings.FrameRate);

            return true;
        }
        catch (Exception ex)
        {
            SetError($"SetExposureSettings failed: {ex.Message}");
            return false;
        }
    }

    public ExposureSettings GetExposureSettings()
    {
        try
        {
            if (_camera == null || !_isConnected)
                return new ExposureSettings();

            return new ExposureSettings
            {
                ExposureTimeUs = GetExposureTime(),
                Gain = GetGainValue(),
                AutoExposure = GetAutoExposure(),
                AutoGain = GetAutoGain(),
                FrameRate = GetFrameRate()
            };
        }
        catch
        {
            return new ExposureSettings();
        }
    }

    public void ClearLastError() => _lastError = string.Empty;

    public bool SetConfiguration(string key, string value)
    {
        try
        {
            if (_camera == null || !_isConnected) return false;
            _camera.Parameters[key].ParseAndSetValue(value);
            return true;
        }
        catch (Exception ex)
        {
            SetError($"SetConfiguration '{key}' failed: {ex.Message}");
            return false;
        }
    }

    public string GetConfiguration(string key)
    {
        try
        {
            if (_camera == null || !_isConnected) return string.Empty;
            return _camera.Parameters[key].ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopGrabbing();
        _camera?.Close();
        _camera?.Dispose();
        _camera = null;
        _converter?.Dispose();
        _converter = null;
    }

    // --- Private helpers ---

    private Basler.Pylon.Camera? CreateCamera()
    {
        return _info.ConnectionMethod switch
        {
            ConnectionMethod.SerialNumber =>
                new Basler.Pylon.Camera(_info.SerialNumber),
            ConnectionMethod.IPAddress =>
                CreateCameraByIp(),
            ConnectionMethod.DeviceIndex =>
                new Basler.Pylon.Camera(
                    CameraFinder.Enumerate().ElementAtOrDefault(_info.DeviceIndex) ?? throw new InvalidOperationException($"No camera at index {_info.DeviceIndex}")),
            _ => // Auto — use serial if available, else first match by ID
                !string.IsNullOrEmpty(_info.SerialNumber)
                    ? new Basler.Pylon.Camera(_info.SerialNumber)
                    : new Basler.Pylon.Camera(
                        CameraFinder.Enumerate().FirstOrDefault(d =>
                            d[CameraInfoKey.SerialNumber] == _info.Id ||
                            d[CameraInfoKey.FriendlyName] == _info.Id) ?? throw new InvalidOperationException($"Camera '{_info.Id}' not found"))
        };
    }

    private Basler.Pylon.Camera CreateCameraByIp()
    {
        // Try enumeration-based lookup by IP
        var devices = CameraFinder.Enumerate();
        var match = devices.FirstOrDefault(d =>
            d.ContainsKey(CameraInfoKey.DeviceIpAddress) &&
            d[CameraInfoKey.DeviceIpAddress] == _info.IpAddress);

        if (match != null)
            return new Basler.Pylon.Camera(match);

        // Try enumeration-based lookup by serial number
        if (!string.IsNullOrEmpty(_info.SerialNumber))
        {
            var serialMatch = devices.FirstOrDefault(d =>
                d[CameraInfoKey.SerialNumber] == _info.SerialNumber);

            if (serialMatch != null)
                return new Basler.Pylon.Camera(serialMatch);

            // Last resort: let Pylon SDK resolve the serial internally
            return new Basler.Pylon.Camera(_info.SerialNumber);
        }

        throw new InvalidOperationException(
            $"Camera at IP {_info.IpAddress} not found by Pylon SDK. " +
            $"Enumerated {devices.Count} device(s). " +
            "Check that the PC has a network adapter on the same subnet as the camera.");
    }

    private void OnImageGrabbed(object? sender, ImageGrabbedEventArgs e)
    {
        var grabResult = e.GrabResult;
        if (!grabResult.GrabSucceeded)
            return;

        _hasNewFrame = true;

        var callback = FrameCallback;
        if (callback == null) return;

        var frame = new CameraFrameData
        {
            CameraId = _info.Id,
            Width = (int)grabResult.Width,
            Height = (int)grabResult.Height,
            Channels = 3,
            FrameNumber = (ulong)grabResult.BlockID,
            Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var size = frame.Width * frame.Height * 3;
        frame.ImageData = new byte[size];
        _converter?.Convert(frame.ImageData, grabResult);

        callback(frame);
    }

    private void FillFrameData(IGrabResult grabResult, CameraFrameData frameData)
    {
        frameData.CameraId = _info.Id;
        frameData.Width = (int)grabResult.Width;
        frameData.Height = (int)grabResult.Height;
        frameData.Channels = 3;
        frameData.FrameNumber = (ulong)grabResult.BlockID;
        frameData.Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var size = frameData.Width * frameData.Height * 3;
        if (frameData.ImageData.Length < size)
            frameData.ImageData = new byte[size];
        _converter?.Convert(frameData.ImageData, grabResult);
    }

    private void SetError(string error) => _lastError = error;

    // Exposure helpers — handle ACE (ExposureTimeAbs) vs ACE2 (ExposureTime)
    private void SetExposureTime(double us)
    {
        if (_camera!.Parameters.Contains(PLCamera.ExposureTime))
            _camera.Parameters[PLCamera.ExposureTime].SetValue(us);
        else if (_camera.Parameters.Contains(PLCamera.ExposureTimeAbs))
            _camera.Parameters[PLCamera.ExposureTimeAbs].SetValue(us);
    }

    private double GetExposureTime()
    {
        if (_camera!.Parameters.Contains(PLCamera.ExposureTime))
            return _camera.Parameters[PLCamera.ExposureTime].GetValue();
        if (_camera.Parameters.Contains(PLCamera.ExposureTimeAbs))
            return _camera.Parameters[PLCamera.ExposureTimeAbs].GetValue();
        return 0;
    }

    private void SetGain(double gain)
    {
        if (_camera!.Parameters.Contains(PLCamera.Gain))
            _camera.Parameters[PLCamera.Gain].SetValue(gain);
        else if (_camera.Parameters.Contains(PLCamera.GainRaw))
            _camera.Parameters[PLCamera.GainRaw].SetValue((long)gain);
    }

    private double GetGainValue()
    {
        if (_camera!.Parameters.Contains(PLCamera.Gain))
            return _camera.Parameters[PLCamera.Gain].GetValue();
        if (_camera.Parameters.Contains(PLCamera.GainRaw))
            return _camera.Parameters[PLCamera.GainRaw].GetValue();
        return 0;
    }

    private void SetAutoExposure(bool enable)
    {
        if (_camera!.Parameters.Contains(PLCamera.ExposureAuto))
            _camera.Parameters[PLCamera.ExposureAuto].SetValue(enable ? "Continuous" : "Off");
    }

    private bool GetAutoExposure()
    {
        if (_camera!.Parameters.Contains(PLCamera.ExposureAuto))
            return _camera.Parameters[PLCamera.ExposureAuto].GetValue() != "Off";
        return false;
    }

    private void SetAutoGain(bool enable)
    {
        if (_camera!.Parameters.Contains(PLCamera.GainAuto))
            _camera.Parameters[PLCamera.GainAuto].SetValue(enable ? "Continuous" : "Off");
    }

    private bool GetAutoGain()
    {
        if (_camera!.Parameters.Contains(PLCamera.GainAuto))
            return _camera.Parameters[PLCamera.GainAuto].GetValue() != "Off";
        return false;
    }

    private void SetFrameRate(double fps)
    {
        // ACE2 uses AcquisitionFrameRate, ACE uses AcquisitionFrameRateAbs
        try
        {
            // Enable frame rate control first
            if (_camera!.Parameters.Contains(PLCamera.AcquisitionFrameRateEnable))
                _camera.Parameters[PLCamera.AcquisitionFrameRateEnable].SetValue(true);

            if (_camera.Parameters.Contains(PLCamera.AcquisitionFrameRate))
                _camera.Parameters[PLCamera.AcquisitionFrameRate].SetValue(fps);
            else if (_camera.Parameters.Contains(PLCamera.AcquisitionFrameRateAbs))
                _camera.Parameters[PLCamera.AcquisitionFrameRateAbs].SetValue(fps);
        }
        catch { /* Frame rate control may not be available */ }
    }

    private double GetFrameRate()
    {
        try
        {
            if (_camera!.Parameters.Contains(PLCamera.AcquisitionFrameRate))
                return _camera.Parameters[PLCamera.AcquisitionFrameRate].GetValue();
            if (_camera.Parameters.Contains(PLCamera.AcquisitionFrameRateAbs))
                return _camera.Parameters[PLCamera.AcquisitionFrameRateAbs].GetValue();
        }
        catch { }
        return 0;
    }
}
