using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// SMU domain of the engine — source-measure units (Keithley 2400 SourceMeter).
/// Exposes engine-owned types only. The measure-only Keithley 6482 stays in
/// <see cref="IMeasurementFacet"/>.
/// </summary>
public interface ISmuFacet
{
    /// <summary>Names of all configured source-measure units.</summary>
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Static device description, or null if the name is unknown.</summary>
    SmuDeviceInfo? GetDevice(string device);

    /// <summary>Last polled state for a device. <see cref="SmuState.Unknown"/> if unknown.</summary>
    SmuState GetState(string device);

    /// <summary>Whether the device's instrument connection is currently open.</summary>
    bool IsConnected(string device);

    /// <summary>Forces an immediate status poll so <see cref="GetState"/> reflects the hardware now.</summary>
    Task RefreshAsync();

    Task<Result> ConnectAsync(string device, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string device);

    /// <summary>Switches to voltage-source mode and sets the level (V).</summary>
    Task<Result> SetSourceVoltageAsync(string device, double volts);

    /// <summary>Switches to current-source mode and sets the level (A).</summary>
    Task<Result> SetSourceCurrentAsync(string device, double amps);

    /// <summary>Sets the compliance limit (current limit when sourcing V, voltage limit when sourcing I).</summary>
    Task<Result> SetComplianceAsync(string device, double value);

    Task<Result> OutputOnAsync(string device);
    Task<Result> OutputOffAsync(string device);

    /// <summary>Raised (edge-triggered) when a device's polled state changes.</summary>
    event Action<SmuStateChanged>? StateChanged;
}
