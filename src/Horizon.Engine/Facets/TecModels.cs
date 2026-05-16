namespace Horizon.Engine;

/// <summary>Static description of a TEC controller.</summary>
public sealed record TecDeviceInfo(string Name, string ControllerType);

/// <summary>
/// Cached state of a TEC controller. <see cref="Valid"/> is false until the
/// first poll.
/// </summary>
public sealed record TecState(
    bool Valid,
    bool Connected,
    double Temperature,
    double SetpointTemperature,
    bool TecOn,
    string LastError)
{
    public static readonly TecState Unknown = new(false, false, 0, 0, false, string.Empty);
}

/// <summary>Raised on <see cref="ITecFacet.StateChanged"/> when a controller's polled state changes.</summary>
public sealed record TecStateChanged(string Device, TecState State);
