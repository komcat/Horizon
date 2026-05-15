namespace Horizon.Engine;

/// <summary>Position of a pneumatic slide (engine-owned mirror of the adapter enum).</summary>
public enum PneumaticPosition
{
    Extended,
    Retracted,
    Extending,
    Retracting,
    Unknown,
    HardwareError,
    Timeout
}

/// <summary>Static description of a pneumatic slide.</summary>
public sealed record PneumaticSlideInfo(string Name);

/// <summary>
/// Cached state of a pneumatic slide. <see cref="Valid"/> is false until the
/// first poll.
/// </summary>
public sealed record PneumaticSlideState(
    bool Valid,
    PneumaticPosition Position,
    bool OutputActive,
    bool ExtendedSensor,
    bool RetractedSensor)
{
    public static readonly PneumaticSlideState Unknown =
        new(false, PneumaticPosition.Unknown, false, false, false);
}

/// <summary>Raised on <see cref="IPneumaticFacet.StateChanged"/> when a slide's polled state changes.</summary>
public sealed record PneumaticStateChanged(string Slide, PneumaticSlideState State);
