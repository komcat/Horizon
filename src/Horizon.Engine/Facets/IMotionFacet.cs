using Horizon.Common;

namespace Horizon.Engine;

/// <summary>
/// Motion domain of the engine — PI GCS2 and ACS SPiiPlus controllers.
/// Exposes engine-owned types only; callers never see <c>Horizon.Motion</c> types.
/// </summary>
public interface IMotionFacet
{
    /// <summary>Names of all configured motion devices.</summary>
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Static device description, or null if the name is unknown.</summary>
    MotionDeviceInfo? GetDevice(string device);

    /// <summary>Last polled state for a device. <see cref="MotionDeviceState.Unknown"/> if unknown.</summary>
    MotionDeviceState GetState(string device);

    /// <summary>Whether the device's controller connection is currently open.</summary>
    bool IsConnected(string device);

    Task<Result> ConnectAsync(string device, CancellationToken ct = default);
    Task<Result> DisconnectAsync(string device);

    /// <summary>Moves an axis to an absolute coordinate; completes when motion settles.</summary>
    Task<Result> MoveAbsoluteAsync(string device, string axis, double position, CancellationToken ct = default);

    /// <summary>
    /// Moves all axes to an absolute multi-axis coordinate (X,Y,Z[,U,V,W]);
    /// completes when motion settles. Layer 1 moves to coordinates only —
    /// name → coordinate resolution is a Layer 2 concern.
    /// </summary>
    Task<Result> MoveToPositionAsync(string device, MotionPosition target, CancellationToken ct = default);

    /// <summary>Moves an axis by a relative distance; completes when motion settles.</summary>
    Task<Result> MoveRelativeAsync(string device, string axis, double distance, CancellationToken ct = default);

    /// <summary>Stops all motion on a device immediately — bypasses the per-device lock.</summary>
    Task<Result> StopAsync(string device);

    /// <summary>Reads the live position directly from the controller.</summary>
    Task<Result<MotionPosition>> GetPositionAsync(string device);

    /// <summary>Sets the velocity for an axis.</summary>
    Task<Result> SetVelocityAsync(string device, string axis, double velocity);

    /// <summary>Reads the current velocity setting for an axis.</summary>
    Task<Result<double>> GetVelocityAsync(string device, string axis);

    /// <summary>
    /// Sets the system (path) velocity — the coordinated platform speed for
    /// PI hexapods. Fails on controllers that have no system velocity (ACS).
    /// </summary>
    Task<Result> SetSystemVelocityAsync(string device, double velocity);

    /// <summary>Reads the system (path) velocity — for PI hexapods.</summary>
    Task<Result<double>> GetSystemVelocityAsync(string device);

    /// <summary>
    /// Sets the device velocity in a controller-aware way — PI: the system
    /// (path) velocity; ACS and others: every installed axis.
    /// </summary>
    Task<Result> SetDeviceVelocityAsync(string device, double velocity);

    /// <summary>Raised (edge-triggered) when a device's polled state changes.</summary>
    event Action<MotionStateChanged>? StateChanged;
}
