using Horizon.Camera.Models;

namespace Horizon.Camera.Interfaces;

public interface ICameraHardware : IDisposable
{
    // Core camera operations
    bool Initialize();
    bool Connect();
    bool Disconnect();
    bool StartGrabbing();
    bool StopGrabbing();

    // Status queries
    bool IsConnected { get; }
    bool IsGrabbing { get; }
    bool IsDeviceRemoved { get; }

    // Frame capture
    bool CaptureFrame(CameraFrameData frameData);
    bool HasNewFrame { get; }

    // Camera information
    string CameraId { get; }
    string ModelName { get; }
    string SerialNumber { get; }
    string VendorName { get; }
    CameraType CameraType { get; }

    // Exposure settings
    bool SetExposureSettings(ExposureSettings settings);
    ExposureSettings GetExposureSettings();

    // Frame callback for broadcasting system
    Action<CameraFrameData>? FrameCallback { get; set; }

    // Error handling
    string LastError { get; }
    void ClearLastError();

    // Optional key-value configuration
    bool SetConfiguration(string key, string value) => false;
    string GetConfiguration(string key) => string.Empty;
}
