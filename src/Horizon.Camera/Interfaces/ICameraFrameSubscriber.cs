using Horizon.Camera.Models;

namespace Horizon.Camera.Interfaces;

public interface ICameraFrameSubscriber
{
    void OnNewFrame(CameraFrameData frameData);
    void OnCameraStatusChanged(string cameraId, bool connected, bool grabbing);
    string SubscriberId { get; }
    bool WantsFramesFromCamera(string cameraId);
    int MinFrameIntervalMs { get; }
}
