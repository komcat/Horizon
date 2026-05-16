using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// TEC domain of the engine — standalone thermo-electric cooler controllers
/// (Thorlabs TED4015). Exposes engine-owned types only. The CLD101x (laser +
/// TEC) stays in <see cref="ILaserFacet"/>.
/// </summary>
public interface ITecFacet
{
    /// <summary>Names of all configured TEC controllers.</summary>
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Static device description, or null if the name is unknown.</summary>
    TecDeviceInfo? GetDevice(string device);

    /// <summary>Last polled state for a device. <see cref="TecState.Unknown"/> if unknown.</summary>
    TecState GetState(string device);

    /// <summary>Whether the device's instrument connection is currently open.</summary>
    bool IsConnected(string device);

    /// <summary>Forces an immediate status poll so <see cref="GetState"/> reflects the hardware now.</summary>
    Task RefreshAsync();

    Task<Result> ConnectAsync(string device, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string device);

    /// <summary>Sets the TEC temperature setpoint (°C).</summary>
    Task<Result> SetTemperatureAsync(string device, double celsius);

    Task<Result> TecOnAsync(string device);
    Task<Result> TecOffAsync(string device);

    /// <summary>Raised (edge-triggered) when a device's polled state changes.</summary>
    event Action<TecStateChanged>? StateChanged;
}
