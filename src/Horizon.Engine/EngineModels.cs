namespace Horizon.Engine;

/// <summary>Lifecycle state of the hardware engine.</summary>
public enum EngineState
{
    Created,
    Initialized,
    Connecting,
    Connected,
    ShuttingDown,
    Faulted
}

/// <summary>Cached status of a single hardware device.</summary>
public sealed record DeviceStatus(
    string Domain,
    string Name,
    bool IsConnected,
    string Detail);

/// <summary>
/// Point-in-time view of the engine. Returned synchronously by
/// <see cref="IHorizonEngine.GetStatusSnapshot"/> — never touches hardware.
/// <see cref="Devices"/> is empty in the skeleton; facets populate it later.
/// </summary>
public sealed record HardwareStatusSnapshot(
    EngineState State,
    IReadOnlyList<DeviceStatus> Devices);

/// <summary>Raised on <see cref="IHorizonEngine.StatusChanged"/> when a device's state moves.</summary>
public sealed record HardwareStatusChanged(
    string Domain,
    string Device);
