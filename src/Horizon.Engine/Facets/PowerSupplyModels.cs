namespace Horizon.Engine;

/// <summary>Static description of a power supply.</summary>
public sealed record PowerSupplyDeviceInfo(string Name, string ControllerType, int ChannelCount);

/// <summary>Live readings for one power-supply output channel (1-based).</summary>
public sealed record PowerSupplyChannelState(int Channel, double Voltage, double Current, bool OutputOn);

/// <summary>
/// Cached state of a power supply. <see cref="Valid"/> is false until the first
/// poll.
/// </summary>
public sealed record PowerSupplyState(
    bool Valid,
    bool Connected,
    IReadOnlyList<PowerSupplyChannelState> Channels,
    string LastError)
{
    public static readonly PowerSupplyState Unknown =
        new(false, false, Array.Empty<PowerSupplyChannelState>(), string.Empty);
}

/// <summary>Raised on <see cref="IPowerSupplyFacet.StateChanged"/> when a supply's polled state changes.</summary>
public sealed record PowerSupplyStateChanged(string Device, PowerSupplyState State);
