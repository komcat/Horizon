namespace Horizon.Engine;

/// <summary>Static description of a motion device — does not change after load.</summary>
public sealed record MotionDeviceInfo(
    string Name,
    string ControllerType,
    IReadOnlyList<string> Axes);

/// <summary>A 6-DOF position. Axis index 0..5 maps to X, Y, Z, U, V, W.</summary>
public sealed record MotionPosition(double X, double Y, double Z, double U, double V, double W)
{
    public static readonly MotionPosition Zero = new(0, 0, 0, 0, 0, 0);

    /// <summary>Components ordered X, Y, Z, U, V, W — index aligns with the axis list.</summary>
    public IReadOnlyList<double> Components => new[] { X, Y, Z, U, V, W };
}

/// <summary>
/// Cached motion state for one device. <see cref="Valid"/> is false until first poll.
/// <see cref="EnabledMask"/>: bit i set = axis i (of <see cref="MotionDeviceInfo.Axes"/>)
/// is enabled; null when the controller has no per-axis enable state (PI).
/// <see cref="IsReferenced"/>: every axis referenced; null when the controller does not
/// report referencing (ACS).
/// </summary>
public sealed record MotionDeviceState(
    bool Valid, bool IsMoving, MotionPosition Position, int? EnabledMask = null, bool? IsReferenced = null)
{
    public static readonly MotionDeviceState Unknown = new(false, false, MotionPosition.Zero);
}

/// <summary>A PI hexapod pivot point — the rotation centre (R, S, T) for U/V/W moves.</summary>
public sealed record PivotPoint(double R, double S, double T)
{
    /// <summary>True when every coordinate is within <paramref name="tolerance"/> of <paramref name="other"/>.</summary>
    public bool Matches(PivotPoint other, double tolerance = 1e-4)
        => Math.Abs(R - other.R) <= tolerance
           && Math.Abs(S - other.S) <= tolerance
           && Math.Abs(T - other.T) <= tolerance;

    public override string ToString() => $"R={R:0.####} S={S:0.####} T={T:0.####}";
}

/// <summary>Raised on <see cref="IMotionFacet.StateChanged"/> when a device's polled state changes.</summary>
public sealed record MotionStateChanged(string Device, MotionDeviceState State);
