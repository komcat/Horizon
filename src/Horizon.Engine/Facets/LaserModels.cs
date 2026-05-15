namespace Horizon.Engine;

/// <summary>Static description of a laser controller.</summary>
public sealed record LaserDeviceInfo(string Name, string ControllerType);

/// <summary>
/// Cached state of a laser controller. <see cref="Valid"/> is false until the
/// first poll.
/// </summary>
public sealed record LaserState(
    bool Valid,
    bool Connected,
    bool TecOn,
    bool LaserOn,
    double Temperature,
    double SetpointTemperature,
    double LaserCurrent,
    double SetpointLaserCurrent)
{
    public static readonly LaserState Unknown = new(false, false, false, false, 0, 0, 0, 0);
}

/// <summary>Raised on <see cref="ILaserFacet.StateChanged"/> when a controller's polled state changes.</summary>
public sealed record LaserStateChanged(string Device, LaserState State);
