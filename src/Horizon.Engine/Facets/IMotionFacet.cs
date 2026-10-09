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

    /// <summary>
    /// Enables (servo on) or disables (servo off) one axis. Fails with
    /// <see cref="ErrorCode.NotSupported"/> on controllers without per-axis enable (PI).
    /// The polled state is reported in <see cref="MotionDeviceState.EnabledMask"/>.
    /// </summary>
    Task<Result> SetAxisEnabledAsync(string device, string axis, bool enabled);

    /// <summary>
    /// Sends one raw command line to an ACS controller, exactly as typed in the SPiiPlus
    /// terminal (e.g. <c>START 1, STARTUP</c>, <c>STOP 1</c>); returns the controller's reply.
    /// Fails with <see cref="ErrorCode.NotSupported"/> on other controllers.
    /// </summary>
    Task<Result<string>> SendTerminalCommandAsync(string device, string command);

    /// <summary>Reads whether an on-controller program buffer is executing.</summary>
    Task<Result<bool>> IsBufferRunningAsync(string device, int buffer);

    /// <summary>
    /// References every axis of a PI hexapod (FRF) and completes when the controller
    /// reports all axes referenced and motion has stopped. On cancel or timeout the
    /// device is stopped. The polled state is reported in <see cref="MotionDeviceState.IsReferenced"/>.
    /// </summary>
    Task<Result> ReferenceAsync(string device, CancellationToken ct = default);

    /// <summary>Reads the pivot point (R, S, T) of a PI hexapod.</summary>
    Task<Result<PivotPoint>> GetPivotAsync(string device);

    /// <summary>
    /// Sets the pivot point (R, S, T) of a PI hexapod. The C-887 holds it only until
    /// power-down (it resets to 0,0,0), and may refuse it while U/V/W are not zero.
    /// </summary>
    Task<Result> SetPivotAsync(string device, PivotPoint pivot);

    /// <summary>Raised (edge-triggered) when a device's polled state changes.</summary>
    event Action<MotionStateChanged>? StateChanged;
}
