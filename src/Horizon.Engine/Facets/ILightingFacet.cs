using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Lighting domain of the engine — LED lighting controllers (e.g. CCS PD3).
/// Exposes engine-owned types only. Devices are keyed by their configured id.
/// </summary>
public interface ILightingFacet
{
    /// <summary>Ids of all configured lighting controllers.</summary>
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Static device description, or null if the id is unknown.</summary>
    LightingDeviceInfo? GetDevice(string device);

    /// <summary>Last polled (read-back) state for a device. <see cref="LightingState.Unknown"/> if unknown.</summary>
    LightingState GetState(string device);

    /// <summary>Whether the controller's connection is currently open.</summary>
    bool IsConnected(string device);

    /// <summary>Forces an immediate status poll so <see cref="GetState"/> reflects the hardware now.</summary>
    Task RefreshAsync();

    Task<Result> ConnectAsync(string device, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string device);

    /// <summary>
    /// Sets a channel's intensity (controller-native units, 0..<see cref="LightingDeviceInfo.MaxIntensity"/>)
    /// by front-panel channel number. 0 switches the output off; any other value switches it on.
    /// </summary>
    Task<Result> SetChannelIntensityAsync(string device, int channel, int intensity);

    /// <summary>
    /// Sets every channel whose <see cref="LightingChannelInfo.Name"/> matches
    /// (case-insensitive) to <paramref name="intensity"/>. Fails if no channel
    /// has that name.
    /// </summary>
    Task<Result> SetNamedChannelAsync(string device, string channelName, int intensity);

    /// <summary>Sets every configured channel to <paramref name="intensity"/> (e.g. all-on / all-off).</summary>
    Task<Result> SetAllAsync(string device, int intensity);

    /// <summary>
    /// Sets every channel in <paramref name="group"/> to <paramref name="intensity"/>.
    /// Fails if the group matches no channel.
    /// </summary>
    Task<Result> SetGroupAsync(string device, string group, int intensity);

    /// <summary>
    /// Configures a group per colour: each channel in <paramref name="group"/> is
    /// set to <paramref name="intensitiesByColor"/>[<see cref="LightingChannelInfo.Color"/>],
    /// or to 0 when its colour is not listed.
    /// </summary>
    Task<Result> ConfigureGroupAsync(
        string device, string group, IReadOnlyDictionary<string, int> intensitiesByColor);

    // ---- Lights (percent / colour API) ------------------------------------
    // A "light" is a channel group (e.g. "rgb_light"), or an ungrouped channel
    // addressed by its name (e.g. "collimate_light"). 0 % switches the
    // channel outputs off; percentages outside 0..100 are rejected.

    /// <summary>Sets every channel of <paramref name="light"/> to <paramref name="percent"/> of full scale.</summary>
    Task<Result> SetLightPercentAsync(string device, string light, double percent);

    /// <summary>
    /// Colour call-out: <see cref="LightColor.White"/> drives every channel of the
    /// light at <paramref name="percent"/> (R+G+B mixed on an RGB light);
    /// Red/Green/Blue drive only the channels of that colour and switch the rest
    /// of the light off. Fails if the light has no channel of the requested colour.
    /// </summary>
    Task<Result> SetLightColorAsync(string device, string light, LightColor color, double percent);

    /// <summary>Raised (edge-triggered) when a device's polled state changes.</summary>
    event Action<LightingStateChanged>? StateChanged;
}
