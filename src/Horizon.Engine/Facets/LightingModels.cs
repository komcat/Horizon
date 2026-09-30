namespace Horizon.Engine;

/// <summary>Static description of one lighting channel.</summary>
/// <param name="Channel">Front-panel channel number (CCS PD3: L1 = 1).</param>
/// <param name="Name">Channel name (e.g. "rgb_red").</param>
/// <param name="Group">Light the channel belongs to (e.g. "rgb_light"); empty if ungrouped.</param>
/// <param name="Color">Colour label (e.g. "white", "red"); empty if unused.</param>
public sealed record LightingChannelInfo(int Channel, string Name, string Group, string Color);

/// <summary>Static description of a lighting controller.</summary>
/// <param name="MaxIntensity">Full-scale intensity in controller-native units (100 %).</param>
/// <param name="Lights">
/// Addressable lights: every group name, plus the name of each ungrouped channel.
/// </param>
public sealed record LightingDeviceInfo(
    string DeviceId,
    string DisplayName,
    string ControllerType,
    IReadOnlyList<LightingChannelInfo> Channels,
    int MaxIntensity,
    IReadOnlyList<string> Lights);

/// <summary>Colour call-out for a light (see <see cref="ILightingFacet.SetLightColorAsync"/>).</summary>
public enum LightColor
{
    /// <summary>Every channel of the light — on an RGB light, R+G+B mixed.</summary>
    White,
    Red,
    Green,
    Blue,
}

/// <summary>State of one lighting channel, read back from the controller.</summary>
/// <param name="Intensity">Setpoint in controller-native units.</param>
/// <param name="On">Whether the channel output is switched on.</param>
/// <param name="Percent">Setpoint as a percentage of full scale.</param>
/// <param name="Mode">Controller lighting-mode code (PD3 "00" = continuous); empty if unknown.</param>
public sealed record LightingChannelState(int Channel, string Name, int Intensity, bool On, double Percent, string Mode);

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
