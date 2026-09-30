using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Power-supply domain of the engine — Keysight E36xxx (LXI) and UNI-T
/// UDP3305S-E (LAN / USB) programmable supplies. Exposes engine-owned types
/// only. Channels are 1-based.
/// </summary>
public interface IPowerSupplyFacet
{
    /// <summary>Names of all configured power supplies.</summary>
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Static device description, or null if the name is unknown.</summary>
    PowerSupplyDeviceInfo? GetDevice(string device);

    /// <summary>Last polled state for a device. <see cref="PowerSupplyState.Unknown"/> if unknown.</summary>
    PowerSupplyState GetState(string device);

    /// <summary>Whether the device's instrument connection is currently open.</summary>
    bool IsConnected(string device);

    /// <summary>Forces an immediate status poll so <see cref="GetState"/> reflects the hardware now.</summary>
    Task RefreshAsync();

    Task<Result> ConnectAsync(string device, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string device);

    /// <summary>Sets the output voltage for a 1-based channel.</summary>
    Task<Result> SetVoltageAsync(string device, int channel, double volts);

    /// <summary>Sets the current limit for a 1-based channel.</summary>
    Task<Result> SetCurrentLimitAsync(string device, int channel, double amps);

    /// <summary>Enables a channel's output.</summary>
    Task<Result> OutputOnAsync(string device, int channel);

    /// <summary>Disables a channel's output.</summary>
    Task<Result> OutputOffAsync(string device, int channel);

    /// <summary>Raised (edge-triggered) when a device's polled state changes.</summary>
    event Action<PowerSupplyStateChanged>? StateChanged;
}
