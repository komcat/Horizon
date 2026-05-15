namespace Horizon.Engine;

/// <summary>Static description of a camera.</summary>
public sealed record CameraDeviceInfo(
    string Id,
    string DisplayName,
    string Model,
    string CameraType,
    string SerialNumber);

/// <summary>Camera exposure settings (engine-owned mirror of the adapter type).</summary>
public sealed record CameraExposure(
    double ExposureUs,
    double Gain,
    bool AutoExposure,
    bool AutoGain,
    double FrameRate);

/// <summary>Cached state of a camera. <see cref="Connected"/> false until polled.</summary>
public sealed record CameraDeviceState(
    bool Connected,
    bool Grabbing,
    int Width,
    int Height,
    CameraExposure Exposure,
    long FramesReceived)
{
    public static readonly CameraDeviceState Unknown =
        new(false, false, 0, 0, new CameraExposure(0, 0, false, false, 0), 0);
}

/// <summary>
/// A raw camera frame delivered on <see cref="ICameraFacet.FrameReady"/>.
/// <see cref="Data"/> is the raw pixel buffer — each front-end converts as it
/// sees fit (the engine never touches WPF/BitmapSource).
/// </summary>
public sealed record CameraFrame(
    string Camera,
    int Width,
    int Height,
    int Channels,
    byte[] Data,
    ulong FrameNumber);

/// <summary>Raised on <see cref="ICameraFacet.StateChanged"/> when a camera's polled state changes.</summary>
public sealed record CameraStateChanged(string Camera, CameraDeviceState State);
