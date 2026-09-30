using Horizon.Config;
using Horizon.Lighting.Models;

namespace Horizon.Lighting;

/// <summary>A single LED lighting controller with a fixed set of channels.</summary>
public interface ILightingDevice
{
    bool Connect();
    bool Disconnect();
    bool IsConnected { get; }
    string DeviceId { get; }
    string DisplayName { get; }
    string ControllerType { get; }

    /// <summary>The controller's configured, enabled channels (numbered as on the front panel).</summary>
    IReadOnlyList<LightingChannelConfig> Channels { get; }

    /// <summary>Full-scale intensity in controller-native units (PD3: 255).</summary>
    int MaxIntensity { get; }

    /// <summary>
    /// Sets a channel's intensity (controller-native units, 0..<see cref="MaxIntensity"/>).
    /// 0 switches the channel output off; any other value switches it on.
    /// </summary>
    LightingError SetIntensity(int channel, int intensity);

    /// <summary>Switches a channel output on or off without changing its intensity setpoint.</summary>
    LightingError SetOutput(int channel, bool isOn);

    /// <summary>
    /// Reads the controller's actual channel state into the status cache. A
    /// communication failure closes the connection.
    /// </summary>
    LightingError Poll();

    /// <summary>Cached status — never touches the hardware.</summary>
    LightingStatus GetStatus();
}
