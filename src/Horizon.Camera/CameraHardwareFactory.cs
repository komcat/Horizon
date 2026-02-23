using Horizon.Camera.Adapters;
using Horizon.Camera.Interfaces;
using Horizon.Camera.Models;

namespace Horizon.Camera;

public static class CameraHardwareFactory
{
    public static ICameraHardware Create(CameraInfo info)
    {
        return info.Type switch
        {
            CameraType.Pylon => new PylonCameraAdapter(info),
            CameraType.IDS => new UEyeCameraAdapter(info, info.DeviceIndex),
            _ => throw new ArgumentException($"Unsupported camera type: {info.Type}")
        };
    }

    public static CameraType DetectType(string deviceInfo)
    {
        if (string.IsNullOrEmpty(deviceInfo))
            return CameraType.Unknown;

        var lower = deviceInfo.ToLowerInvariant();

        if (lower.Contains("basler") || lower.Contains("pylon") || lower.Contains("ace"))
            return CameraType.Pylon;

        if (lower.Contains("ids") || lower.Contains("ueye") || lower.Contains("ui-") || lower.Contains("peak"))
            return CameraType.IDS;

        return CameraType.Unknown;
    }
}
