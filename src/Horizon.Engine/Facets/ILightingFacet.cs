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

    /// <summary>Last polled state for a device. <see cref="LightingState.Unknown"/> if unknown.</summary>
    LightingState GetState(string device);

    /// <summary>Whether the controller's connection is currently open.</summary>
    bool IsConnected(string device);

    Task<Result> ConnectAsync(string device, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string device);

    /// <summary>Sets a channel's intensity (controller-native units).</summary>
    Task<Result> SetChannelIntensityAsync(string device, int channel, int intensity);

    /// <summary>Raised (edge-triggered) when a device's polled state changes.</summary>
    event Action<LightingStateChanged>? StateChanged;
}
