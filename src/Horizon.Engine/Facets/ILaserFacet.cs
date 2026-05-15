using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Laser-controller domain of the engine — Thorlabs CLD101x (TEC + laser diode).
/// Exposes engine-owned types only.
/// </summary>
public interface ILaserFacet
{
    /// <summary>Names of all configured laser controllers.</summary>
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Static device description, or null if the name is unknown.</summary>
    LaserDeviceInfo? GetDevice(string device);

    /// <summary>Last polled state for a device. <see cref="LaserState.Unknown"/> if unknown.</summary>
    LaserState GetState(string device);

    /// <summary>Whether the device's instrument connection is currently open.</summary>
    bool IsConnected(string device);

    Task<Result> ConnectAsync(string device, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string device);

    /// <summary>Sets the TEC temperature setpoint (°C).</summary>
    Task<Result> SetTemperatureAsync(string device, double celsius);
    Task<Result> TecOnAsync(string device);
    Task<Result> TecOffAsync(string device);

    /// <summary>Sets the laser diode current setpoint (mA).</summary>
    Task<Result> SetLaserCurrentAsync(string device, double milliamps);

    /// <summary>Enables laser diode emission.</summary>
    Task<Result> LaserOnAsync(string device);

    /// <summary>Disables laser diode emission.</summary>
    Task<Result> LaserOffAsync(string device);

    /// <summary>Raised (edge-triggered) when a device's polled state changes.</summary>
    event Action<LaserStateChanged>? StateChanged;
}
