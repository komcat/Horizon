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

    /// <summary>Takes one fresh measurement from a channel (1-based), bypassing the poll cache.</summary>
    Task<Result<SmuReading>> ReadMeasurementAsync(string device, int channel);

    /// <summary>Switches the instrument between fast (low-NPLC) and normal measurement mode.</summary>
    Task<Result> SetFastModeAsync(string device, bool fast);

    /// <summary>Begins continuous sampling of a channel (1-based) at the given interval (ms).
    /// Channels of one device stream independently.</summary>
    Task<Result> StartStreamingAsync(string device, int channel, int intervalMs);

    /// <summary>Stops streaming on one channel; restores normal mode once the
    /// device has no channel still streaming. Idempotent.</summary>
    Task<Result> StopStreamingAsync(string device, int channel);

    /// <summary>Stops streaming on every channel of a device. Idempotent.</summary>
    Task<Result> StopStreamingAsync(string device);

    /// <summary>Whether any channel of the device is currently streaming.</summary>
    bool IsStreaming(string device);

    /// <summary>Whether the given channel of the device is currently streaming.</summary>
    bool IsStreaming(string device, int channel);

    /// <summary>Raised once per streamed sample, on a background thread.</summary>
    event Action<SmuSample>? Sampled;

    /// <summary>Raised (edge-triggered) when a device's polled state changes.</summary>
    event Action<SmuStateChanged>? StateChanged;
}
