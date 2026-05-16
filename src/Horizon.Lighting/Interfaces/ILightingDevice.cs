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

    /// <summary>The controller's configured channels.</summary>
    IReadOnlyList<LightingChannelConfig> Channels { get; }

    /// <summary>Sets a channel's intensity (controller-native units).</summary>
    LightingError SetIntensity(int channel, int intensity);

    LightingStatus GetStatus();
}
