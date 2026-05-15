using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Current-measurement domain of the engine — Keithley 6482 picoammeters.
/// Exposes engine-owned types only.
/// </summary>
public interface IMeasurementFacet
{
    /// <summary>Names of all configured measurement devices.</summary>
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Static device description, or null if the name is unknown.</summary>
    MeasurementDeviceInfo? GetDevice(string device);

    /// <summary>Last polled readings for a device. <see cref="MeasurementState.Unknown"/> if unknown.</summary>
    MeasurementState GetState(string device);

    /// <summary>Whether the device's instrument connection is currently open.</summary>
    bool IsConnected(string device);

    Task<Result> ConnectAsync(string device, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string device);

    /// <summary>Sets the source voltage.</summary>
    Task<Result> SetVoltageAsync(string device, double volts);

    /// <summary>Enables or disables the source voltage output.</summary>
    Task<Result> SetVoltageEnabledAsync(string device, bool enabled);

    /// <summary>Resets the instrument (`*RST`).</summary>
    Task<Result> ResetAsync(string device);

    /// <summary>Reads the instrument identity string (`*IDN?`).</summary>
    Task<Result<string>> GetIdentityAsync(string device);

    /// <summary>Raised on each poll update with fresh channel readings.</summary>
    event Action<MeasurementChanged>? StateChanged;
}
