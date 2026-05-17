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

    /// <summary>Whether the device is fast-streaming — continuous low-NPLC reads (~20–80 Hz).</summary>
    bool IsStreaming(string device);

    /// <summary>
    /// Switches the device to fast continuous reads (NPLC 0.1, `:INIT:CONT ON`).
    /// One `:READ?` returns every channel; readings still arrive via
    /// <see cref="StateChanged"/>, just much faster than the normal poll.
    /// </summary>
    Task<Result> StartStreamingAsync(string device);

    /// <summary>Stops fast streaming, restores normal mode, and resumes the normal-rate poll. Idempotent.</summary>
    Task<Result> StopStreamingAsync(string device);

    /// <summary>Reads the instrument identity string (`*IDN?`).</summary>
    Task<Result<string>> GetIdentityAsync(string device);

    /// <summary>Raised on each poll update with fresh channel readings.</summary>
    event Action<MeasurementChanged>? StateChanged;
}
