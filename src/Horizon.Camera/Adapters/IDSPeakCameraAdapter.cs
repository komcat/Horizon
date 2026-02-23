using Horizon.Camera.Interfaces;
using Horizon.Camera.Models;
using peak.core;
using peak.core.nodes;
using peak.ipl;
using PeakBuffer = peak.core.Buffer;
using PeakTimeout = peak.core.Timeout;

namespace Horizon.Camera.Adapters;

public sealed class IDSPeakCameraAdapter : ICameraHardware
{
    private readonly CameraInfo _info;
    private DeviceDescriptor? _deviceDescriptor;
    private Device? _device;
    private DataStream? _dataStream;
    private NodeMap? _remoteNodeMap;
    private ImageConverter? _imageConverter;
    private volatile bool _isConnected;
    private volatile bool _isGrabbing;
    private volatile bool _isDeviceRemoved;
    private volatile bool _hasNewFrame;
    private string _lastError = string.Empty;
    private CancellationTokenSource? _grabCts;
    private Task? _grabTask;
    private bool _disposed;

    public IDSPeakCameraAdapter(CameraInfo info)
    {
        _info = info;
    }

    public bool IsConnected => _isConnected;
    public bool IsGrabbing => _isGrabbing;
    public bool IsDeviceRemoved => _isDeviceRemoved;
    public bool HasNewFrame => _hasNewFrame;
    public string CameraId => _info.Id;
    public CameraType CameraType => CameraType.IDS;
    public string LastError => _lastError;
    public Action<CameraFrameData>? FrameCallback { get; set; }

    public string ModelName
    {
        get
        {
            try { return _remoteNodeMap?.FindNode<StringNode>("DeviceModelName")?.Value() ?? _info.ModelName; }
            catch { return _info.ModelName; }
        }
    }

    public string SerialNumber
    {
        get
        {
            try { return _remoteNodeMap?.FindNode<StringNode>("DeviceSerialNumber")?.Value() ?? _info.SerialNumber; }
            catch { return _info.SerialNumber; }
        }
    }

    public string VendorName
    {
        get
        {
            try { return _remoteNodeMap?.FindNode<StringNode>("DeviceVendorName")?.Value() ?? "IDS Imaging"; }
            catch { return "IDS Imaging"; }
        }
    }

    public bool Initialize()
    {
        try
        {
            peak.Library.Initialize();
            return true;
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
            if (!Initialize())
                return false;

            var deviceManager = peak.DeviceManager.Instance();

            // Load uEye GenTL producer so peak can see legacy uEye cameras
            var ueyeCti = @"C:\Program Files\IDS\ids_peak\ids_ueyegentl\64\ids_ueyegentl.cti";
            if (File.Exists(ueyeCti))
            {
                try { deviceManager.AddProducerLibrary(ueyeCti); } catch { }
            }

            deviceManager.Update();

            _deviceDescriptor = FindDeviceDescriptor(deviceManager);
            if (_deviceDescriptor == null)
            {
                SetError("Device not found");
                return false;
            }

            _device = _deviceDescriptor.OpenDevice(DeviceAccessType.Control);

            // Open data stream
            var dataStreams = _device.DataStreams();
            if (dataStreams.Count > 0)
            {
                _dataStream = dataStreams[0].OpenDataStream();
            }

            _remoteNodeMap = _device.RemoteDevice().NodeMaps()[0];

            // Initialize image converter for RGB8 output
            _imageConverter = new ImageConverter();

            _isConnected = true;
            _isDeviceRemoved = false;
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

            _imageConverter = null;
            _remoteNodeMap = null;
            _dataStream = null;
            _device = null;
            _deviceDescriptor = null;

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
            if (_dataStream == null || _remoteNodeMap == null)
                return false;

            // Allocate and announce buffers
            var payloadSize = _remoteNodeMap.FindNode<IntegerNode>("PayloadSize").Value();
            var bufferCount = _dataStream.NumBuffersAnnouncedMinRequired();
            for (uint i = 0; i < bufferCount; i++)
            {
                var buffer = _dataStream.AllocAndAnnounceBuffer((uint)payloadSize, IntPtr.Zero);
                _dataStream.QueueBuffer(buffer);
            }

            _dataStream.StartAcquisition();

            var acquisitionStart = _remoteNodeMap.FindNode<CommandNode>("AcquisitionStart");
            acquisitionStart.Execute();
            acquisitionStart.WaitUntilDone();

            _isGrabbing = true;

            // Start background grab task
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
            if (!_isGrabbing)
                return true;

            _isGrabbing = false;
            _grabCts?.Cancel();

            try { _grabTask?.Wait(2000); } catch { }
            _grabCts?.Dispose();
            _grabCts = null;
            _grabTask = null;

            try
            {
                var acquisitionStop = _remoteNodeMap?.FindNode<CommandNode>("AcquisitionStop");
                acquisitionStop?.Execute();
                acquisitionStop?.WaitUntilDone();
            }
            catch { }

            try
            {
                _dataStream?.StopAcquisition(AcquisitionStopMode.Default);
                _dataStream?.Flush(DataStreamFlushMode.DiscardAll);

                // Revoke all buffers
                var announced = _dataStream?.AnnouncedBuffers();
                if (announced != null)
                {
                    for (int i = 0; i < announced.Count; i++)
                    {
                        _dataStream?.RevokeBuffer(announced[i]);
                    }
                }
            }
            catch { }

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
            if (_dataStream == null || !_isConnected)
                return false;

            var buffer = _dataStream.WaitForFinishedBuffer(new PeakTimeout(5000));
            if (buffer == null)
                return false;

            ConvertBufferToFrame(buffer, frameData);
            _dataStream.QueueBuffer(buffer);
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
            if (_remoteNodeMap == null || !_isConnected)
                return false;

            SetExposureTime(settings.ExposureTimeUs);
            SetGain(settings.Gain);
            SetAutoExposure(settings.AutoExposure);
            SetAutoGain(settings.AutoGain);

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
            if (_remoteNodeMap == null || !_isConnected)
                return new ExposureSettings();

            return new ExposureSettings
            {
                ExposureTimeUs = GetExposureTime(),
                Gain = GetGainValue(),
                AutoExposure = GetAutoExposure(),
                AutoGain = GetAutoGain()
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
            if (_remoteNodeMap == null) return false;
            var node = _remoteNodeMap.FindNode<StringNode>(key);
            node.SetValue(value);
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
            if (_remoteNodeMap == null) return string.Empty;
            return _remoteNodeMap.FindNode<StringNode>(key).Value();
        }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        peak.Library.Close();
    }

    // --- Private helpers ---

    private DeviceDescriptor? FindDeviceDescriptor(peak.DeviceManager deviceManager)
    {
        var devices = deviceManager.Devices();
        for (int i = 0; i < devices.Count; i++)
        {
            var dev = devices[i];
            var serial = dev.SerialNumber();
            var displayName = dev.DisplayName();

            switch (_info.ConnectionMethod)
            {
                case ConnectionMethod.SerialNumber:
                    if (serial == _info.SerialNumber) return dev;
                    break;
                case ConnectionMethod.DeviceIndex:
                    if (i == _info.DeviceIndex) return dev;
                    break;
                case ConnectionMethod.IPAddress:
                    if (displayName.Contains(_info.IpAddress)) return dev;
                    break;
                default:
                    if (serial == _info.Id || displayName == _info.Id ||
                        serial == _info.SerialNumber)
                        return dev;
                    break;
            }
        }

        // Fallback: return first device if auto
        if (_info.ConnectionMethod == ConnectionMethod.Auto && devices.Count > 0)
            return devices[0];

        return null;
    }

    private void GrabLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _isGrabbing)
        {
            try
            {
                var buffer = _dataStream?.WaitForFinishedBuffer(new PeakTimeout(1000));
                if (buffer == null) continue;

                _hasNewFrame = true;

                var callback = FrameCallback;
                if (callback != null)
                {
                    var frame = new CameraFrameData();
                    ConvertBufferToFrame(buffer, frame);
                    callback(frame);
                }

                _dataStream?.QueueBuffer(buffer);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Timeout or transient error, continue grabbing
            }
        }
    }

    private void ConvertBufferToFrame(PeakBuffer buffer, CameraFrameData frameData)
    {
        frameData.CameraId = _info.Id;
        frameData.Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        frameData.FrameNumber++;

        // Create IDS peak IPL image from buffer
        var inputFormat = new PixelFormat((PixelFormatName)buffer.PixelFormat());
        var image = new peak.ipl.Image(inputFormat,
            buffer.BasePtr(), buffer.Size(), buffer.Width(), buffer.Height());

        // Convert to RGB8
        var rgb8Format = new PixelFormat(PixelFormatName.RGB8);
        var rgbImage = _imageConverter!.Convert(image, rgb8Format);

        frameData.Width = (int)rgbImage.Width();
        frameData.Height = (int)rgbImage.Height();
        frameData.Channels = 3;

        var size = frameData.Width * frameData.Height * 3;
        if (frameData.ImageData.Length < size)
            frameData.ImageData = new byte[size];

        System.Runtime.InteropServices.Marshal.Copy(rgbImage.Data(), frameData.ImageData, 0, size);
    }

    private void SetError(string error) => _lastError = error;

    // GenICam exposure/gain via remote node map
    private void SetExposureTime(double us)
    {
        try { _remoteNodeMap!.FindNode<FloatNode>("ExposureTime").SetValue(us); } catch { }
    }

    private double GetExposureTime()
    {
        try { return _remoteNodeMap!.FindNode<FloatNode>("ExposureTime").Value(); } catch { return 0; }
    }

    private void SetGain(double gain)
    {
        try { _remoteNodeMap!.FindNode<FloatNode>("Gain").SetValue(gain); } catch { }
    }

    private double GetGainValue()
    {
        try { return _remoteNodeMap!.FindNode<FloatNode>("Gain").Value(); } catch { return 0; }
    }

    private void SetAutoExposure(bool enable)
    {
        try
        {
            var node = _remoteNodeMap!.FindNode<EnumerationNode>("ExposureAuto");
            node.SetCurrentEntry(node.FindEntry(enable ? "Continuous" : "Off"));
        }
        catch { }
    }

    private bool GetAutoExposure()
    {
        try
        {
            return _remoteNodeMap!.FindNode<EnumerationNode>("ExposureAuto")
                .CurrentEntry().SymbolicValue() != "Off";
        }
        catch { return false; }
    }

    private void SetAutoGain(bool enable)
    {
        try
        {
            var node = _remoteNodeMap!.FindNode<EnumerationNode>("GainAuto");
            node.SetCurrentEntry(node.FindEntry(enable ? "Continuous" : "Off"));
        }
        catch { }
    }

    private bool GetAutoGain()
    {
        try
        {
            return _remoteNodeMap!.FindNode<EnumerationNode>("GainAuto")
                .CurrentEntry().SymbolicValue() != "Off";
        }
        catch { return false; }
    }
}
