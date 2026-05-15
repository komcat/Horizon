namespace Horizon.Engine;

/// <summary>A named IO pin and its index within its bank.</summary>
public sealed record IoPin(int Pin, string Name);

/// <summary>Static description of an IO device — does not change after load.</summary>
public sealed record IoDeviceInfo(
    string Name,
    int InputCount,
    int OutputCount,
    IReadOnlyList<IoPin> InputPins,
    IReadOnlyList<IoPin> OutputPins);

/// <summary>
/// Cached IO state for one device. <see cref="Inputs"/> / <see cref="Outputs"/>
/// are bitmasks (bit N = pin N). <see cref="Valid"/> is false until the first
/// successful poll.
/// </summary>
public sealed record IoState(bool Valid, uint Inputs, uint Outputs)
{
    public static readonly IoState Unknown = new(false, 0u, 0u);
}

/// <summary>Raised on <see cref="IIOFacet.InputChanged"/> when a device's polled state changes.</summary>
public sealed record IoInputChanged(string Device, IoState State);
