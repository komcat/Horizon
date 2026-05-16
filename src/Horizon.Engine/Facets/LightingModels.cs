namespace Horizon.Engine;

/// <summary>Static description of one lighting channel.</summary>
public sealed record LightingChannelInfo(int Channel, string Name);

/// <summary>Static description of a lighting controller.</summary>
public sealed record LightingDeviceInfo(
    string DeviceId,
    string DisplayName,
    string ControllerType,
    IReadOnlyList<LightingChannelInfo> Channels);

/// <summary>Last-commanded state of one lighting channel.</summary>
public sealed record LightingChannelState(int Channel, string Name, int Intensity);

/// <summary>
/// Cached state of a lighting controller. <see cref="Valid"/> is false until
/// the first poll.
/// </summary>
public sealed record LightingState(
    bool Valid,
    bool Connected,
    IReadOnlyList<LightingChannelState> Channels,
    string LastError)
{
    public static readonly LightingState Unknown =
        new(false, false, Array.Empty<LightingChannelState>(), string.Empty);
}

/// <summary>Raised on <see cref="ILightingFacet.StateChanged"/> when a controller's polled state changes.</summary>
public sealed record LightingStateChanged(string Device, LightingState State);
