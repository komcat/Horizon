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

/// <summary>
/// One axis's motion profile read from an ACS controller — VEL, ACC, DEC, JERK and KDEC, in
/// controller units (mm/s, mm/s², mm/s², mm/s³, mm/s²).
/// </summary>
public sealed record AxisMotionProfile(
    string Axis, double Velocity, double Acceleration, double Deceleration, double Jerk, double KillDeceleration);

/// <summary>
/// ACS adaptive-profile ratios: every velocity set also writes ACC = DEC = <see cref="AccPerVelocity"/> × VEL,
/// JERK = <see cref="JerkPerAcc"/> × ACC and KDEC = <see cref="KdecPerAcc"/> × ACC.
/// Saved per device in <c>acs_profile_ratios.json</c>.
/// </summary>
public sealed record MotionProfileRatios(double AccPerVelocity, double JerkPerAcc, double KdecPerAcc)
{
    /// <summary>Largest accepted ratio — a sanity bound against a typo.</summary>
    public const double MaxRatio = 1000.0;

    /// <summary>The built-in ratios: ACC/DEC = 10 × VEL, JERK = 10 × ACC, KDEC = 2 × ACC.</summary>
    public static readonly MotionProfileRatios Default = new(10.0, 10.0, 2.0);

    /// <summary>Null when every ratio is finite, &gt; 0 and ≤ <see cref="MaxRatio"/>; else why not.</summary>
    public string? Validate()
    {
        if (!Ok(AccPerVelocity)) return $"ACC/DEC ratio must be > 0 and ≤ {MaxRatio:0}.";
        if (!Ok(JerkPerAcc)) return $"JERK ratio must be > 0 and ≤ {MaxRatio:0}.";
        if (!Ok(KdecPerAcc)) return $"KDEC ratio must be > 0 and ≤ {MaxRatio:0}.";
        return null;
    }

    private static bool Ok(double r) => double.IsFinite(r) && r > 0 && r <= MaxRatio;

    public override string ToString()
        => $"ACC/DEC = {AccPerVelocity:G6}×VEL, JERK = {JerkPerAcc:G6}×ACC, KDEC = {KdecPerAcc:G6}×ACC";
}

/// <summary>Raised on <see cref="IMotionFacet.StateChanged"/> when a device's polled state changes.</summary>
public sealed record MotionStateChanged(string Device, MotionDeviceState State);
