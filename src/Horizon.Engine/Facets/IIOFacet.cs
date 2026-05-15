using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Digital IO domain of the engine — Fastech Ezi-IO modules and motor-built-in IO.
/// Exposes engine-owned types only; callers never see <c>Horizon.IO</c> types.
/// </summary>
public interface IIOFacet
{
    /// <summary>Names of all configured IO devices.</summary>
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Static device description, or null if the name is unknown.</summary>
    IoDeviceInfo? GetDevice(string device);

    /// <summary>Last polled state for a device. <see cref="IoState.Unknown"/> if unknown.</summary>
    IoState GetState(string device);

    /// <summary>Whether the device's hardware connection is currently open.</summary>
    bool IsConnected(string device);

    Task<Result> ConnectAsync(string device, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string device);

    /// <summary>Sets an output pin by zero-based index.</summary>
    Task<Result> SetOutputAsync(string device, int pin, bool state, CancellationToken ct = default);

    /// <summary>Sets an output pin by configured name.</summary>
    Task<Result> SetOutputByNameAsync(string device, string pinName, bool state, CancellationToken ct = default);

    /// <summary>Raised (edge-triggered) when a device's polled state changes.</summary>
    event Action<IoInputChanged>? InputChanged;
}
