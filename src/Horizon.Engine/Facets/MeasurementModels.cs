namespace Horizon.Engine;

/// <summary>Static description of a current-measurement device.</summary>
public sealed record MeasurementDeviceInfo(
    string Name,
    int ChannelCount,
    IReadOnlyList<int> EnabledChannels);

/// <summary>
/// Cached readings for a current-measurement device. <see cref="Channels"/> is
/// indexed 0-based (channel 1 = index 0). <see cref="Valid"/> is false until the
/// first poll.
/// </summary>
public sealed record MeasurementState(bool Valid, IReadOnlyList<double> Channels, string Unit)
{
    public static readonly MeasurementState Unknown = new(false, Array.Empty<double>(), "A");
}

/// <summary>Raised on <see cref="IMeasurementFacet.StateChanged"/> on each poll update.</summary>
public sealed record MeasurementChanged(string Device, MeasurementState State);
