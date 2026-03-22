using Horizon.Camera.Interfaces;
using Horizon.Camera.Models;

namespace Horizon.Camera;

public sealed class LiveVideoSubscriber : ICameraFrameSubscriber
{
    private readonly object _frameLock = new();
    private CameraFrameData? _latestFrame;
    private volatile bool _hasNewFrame;
    private volatile bool _cameraConnected;
    private volatile bool _cameraGrabbing;
    private long _totalFramesReceived;
    private string _targetCameraId;
    private readonly string _componentName;

    public LiveVideoSubscriber(string cameraId, string componentName = "", int minFrameIntervalMs = 0)
    {
        _targetCameraId = cameraId;
        _componentName = componentName;
        MinFrameIntervalMs = minFrameIntervalMs;
        SubscriberId = string.IsNullOrEmpty(componentName)
            ? $"LiveVideo_{cameraId}"
            : $"LiveVideo_{componentName}_{cameraId}";
    }

    public string SubscriberId { get; private set; }
    public int MinFrameIntervalMs { get; }
    public bool HasNewFrame => _hasNewFrame;
    public bool IsCameraConnected => _cameraConnected;
    public bool IsCameraGrabbing => _cameraGrabbing;
    public long TotalFramesReceived => Interlocked.Read(ref _totalFramesReceived);

    public void OnNewFrame(CameraFrameData frameData)
    {
        lock (_frameLock)
        {
            _latestFrame = frameData;
            _hasNewFrame = true;
        }
        Interlocked.Increment(ref _totalFramesReceived);
    }

    public void OnCameraStatusChanged(string cameraId, bool connected, bool grabbing)
    {
        if (cameraId != _targetCameraId) return;
        _cameraConnected = connected;
        _cameraGrabbing = grabbing;
    }

    public bool WantsFramesFromCamera(string cameraId) => cameraId == _targetCameraId;

    public CameraFrameData? GetLatestFrame()
    {
        lock (_frameLock)
        {
            return _latestFrame;
        }
    }

    /// <summary>
    /// Copy latest frame data into the provided buffer. Returns false if no frame available.
    /// </summary>
    public bool GetLatestFrameInto(byte[] buffer, out int width, out int height, out int channels)
    {
        lock (_frameLock)
        {
            if (_latestFrame == null || !_hasNewFrame)
            {
                width = height = channels = 0;
                return false;
            }

            width = _latestFrame.Width;
            height = _latestFrame.Height;
            channels = _latestFrame.Channels;

            var size = width * height * channels;
            if (buffer.Length < size)
            {
                width = height = channels = 0;
                return false;
            }

            Buffer.BlockCopy(_latestFrame.ImageData, 0, buffer, 0, size);
            _hasNewFrame = false;
            return true;
        }
    }

    public void MarkFrameConsumed() => _hasNewFrame = false;

    public void SetTargetCamera(string cameraId)
    {
        _targetCameraId = cameraId;
        SubscriberId = string.IsNullOrEmpty(_componentName)
            ? $"LiveVideo_{cameraId}"
            : $"LiveVideo_{_componentName}_{cameraId}";
        ResetState();
    }

    public string GetTargetCamera() => _targetCameraId;

    public void InitializeStatusFromManager(CameraManager manager)
    {
        var status = manager.GetCameraStatus(_targetCameraId);
        _cameraConnected = status.Connected;
        _cameraGrabbing = status.Grabbing;
    }

    private void ResetState()
    {
        lock (_frameLock)
        {
            _latestFrame = null;
            _hasNewFrame = false;
        }
        _cameraConnected = false;
        _cameraGrabbing = false;
        Interlocked.Exchange(ref _totalFramesReceived, 0);
    }
}
