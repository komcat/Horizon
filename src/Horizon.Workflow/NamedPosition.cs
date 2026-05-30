using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// A named position in a product: a device coordinate plus optional motion and
/// camera settings applied during a recall move (see
/// <c>IProcessService.MoveToNamedPositionAsync</c>).
/// <list type="bullet">
/// <item><see cref="Velocity"/> = 0 means "leave the device velocity unchanged";
/// a non-zero value is applied (controller-aware) <b>before</b> the move.</item>
/// <item><see cref="Camera"/> + <see cref="ExposureUs"/>: when both are set, the
/// camera's exposure is changed to <see cref="ExposureUs"/> microseconds
/// <b>after the move starts</b> (concurrently with travel). Null = leave the
/// camera untouched.</item>
/// </list>
/// </summary>
public sealed record NamedPosition(
    string Device,
    string Name,
    MotionPosition Coordinates,
    double Velocity,
    string? Camera = null,
    double? ExposureUs = null);
