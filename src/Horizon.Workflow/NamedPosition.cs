using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// A named position in a product: a device coordinate plus an optional velocity
/// to apply to the device before moving there.
/// <see cref="Velocity"/> = 0 means "leave the device velocity unchanged";
/// a non-zero value is applied (controller-aware) before the recall move.
/// </summary>
public sealed record NamedPosition(
    string Device,
    string Name,
    MotionPosition Coordinates,
    double Velocity);
