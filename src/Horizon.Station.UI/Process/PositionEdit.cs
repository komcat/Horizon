using Horizon.Engine;

namespace Horizon.Station.UI.Process;

/// <summary>
/// Input to <c>PositionEditorWindow</c> — describes one process position to edit.
/// <see cref="Axes"/> drives which axis fields the editor shows (e.g. an ACS
/// 3-axis gantry yields X/Y/Z only; a PI hexapod yields X/Y/Z/U/V/W).
/// </summary>
public sealed class PositionEditContext
{
    public required string Position { get; init; }
    public required string Device { get; init; }
    public required string ControllerType { get; init; }
    public required IReadOnlyList<string> Axes { get; init; }
    public required double Velocity { get; init; }
    public required MotionPosition Pose { get; init; }

    /// <summary>Camera ids available to bind this position's exposure to (for the picker).</summary>
    public required IReadOnlyList<string> Cameras { get; init; }

    /// <summary>Currently-bound camera id, or null if none.</summary>
    public string? Camera { get; init; }

    /// <summary>Current target exposure in microseconds, or null if none.</summary>
    public double? ExposureUs { get; init; }
}

/// <summary>Result the editor hands back on Apply — the new recall settings for a position.</summary>
public sealed record PositionEditResult(
    string Position, string Device, double Velocity, MotionPosition Pose,
    string? Camera, double? ExposureUs);
