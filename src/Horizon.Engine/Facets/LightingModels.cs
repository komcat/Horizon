namespace Horizon.Engine;

/// <summary>Static description of one lighting channel.</summary>
/// <param name="Channel">Front-panel channel number (CCS PD3: L1 = 1).</param>
/// <param name="Name">Channel name (e.g. "rgb_red").</param>
/// <param name="Group">Light the channel belongs to (e.g. "main_camera_light"); empty if ungrouped.</param>
/// <param name="Color">Colour label (e.g. "red", "collimate"); empty if unused.</param>
/// <param name="Camera">Camera id the channel illuminates (e.g. "main_camera"); empty if not tied to a camera.</param>
public sealed record LightingChannelInfo(int Channel, string Name, string Group, string Color, string Camera);

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

/// <summary>
/// Colour call-out for a light (see <see cref="ILightingFacet.SetLightColorAsync"/>).
/// Each call-out drives the light's channels whose configured colour it matches
/// (<see cref="LightColors.Matches"/>) and switches the rest of the light off.
/// </summary>
public enum LightColor
{
    /// <summary>R+G+B mixed white — every red, green and blue channel of the light.</summary>
    White,
    Red,
    Green,
    Blue,
    /// <summary>The collimated light — channels configured with colour "collimate".</summary>
    Collimate,
    /// <summary><see cref="White"/> and <see cref="Collimate"/> together — collimate + R + G + B.</summary>
    WhiteCollimate,
}

/// <summary>Parsing and channel matching for <see cref="LightColor"/> call-outs.</summary>
public static class LightColors
{
    /// <summary>Call-out names in display order (lower-case, as used in steps and scripts).</summary>
    public static readonly IReadOnlyList<string> Names =
        new[] { "red", "green", "blue", "white", "collimate", "white_collimate" };

    /// <summary>
    /// Parses a call-out name (case-insensitive). "white_collimate" also accepts
    /// "white collimate" / "whitecollimate". Numeric strings are rejected.
    /// </summary>
    public static bool TryParse(string? text, out LightColor color)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "white": color = LightColor.White; return true;
            case "red": color = LightColor.Red; return true;
            case "green": color = LightColor.Green; return true;
            case "blue": color = LightColor.Blue; return true;
            case "collimate": color = LightColor.Collimate; return true;
            case "white_collimate" or "white collimate" or "whitecollimate":
                color = LightColor.WhiteCollimate; return true;
            default: color = LightColor.White; return false;
        }
    }

    /// <summary>Operator-facing label, e.g. "White + collimate".</summary>
    public static string Display(LightColor color) => color switch
    {
        LightColor.White => "White (RGB)",
        LightColor.WhiteCollimate => "White + collimate",
        _ => color.ToString(),
    };

    /// <summary>Whether a channel with configured colour <paramref name="channelColor"/> is driven by <paramref name="callout"/>.</summary>
    public static bool Matches(LightColor callout, string channelColor) => callout switch
    {
        LightColor.White => IsPrimary(channelColor),
        LightColor.WhiteCollimate => IsPrimary(channelColor) || IsCollimate(channelColor),
        _ => channelColor.Equals(callout.ToString(), StringComparison.OrdinalIgnoreCase),
    };

    private static bool IsCollimate(string channelColor)
        => channelColor.Equals("collimate", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for "red", "green" or "blue" — the channels mixed for white.</summary>
    public static bool IsPrimary(string channelColor)
        => channelColor.Equals("red", StringComparison.OrdinalIgnoreCase)
           || channelColor.Equals("green", StringComparison.OrdinalIgnoreCase)
           || channelColor.Equals("blue", StringComparison.OrdinalIgnoreCase);
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
