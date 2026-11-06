namespace Horizon.Motion.Models;

public record PositionStruct
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double U { get; init; }
    public double V { get; init; }
    public double W { get; init; }
    public List<string> Waypoints { get; init; } = new();
    public List<string> ExitWaypoints { get; init; } = new();
}

/// <summary>One axis's motion profile as the controller holds it (ACS: VEL, ACC, DEC, JERK, KDEC).</summary>
public sealed record AxisProfile(
    double Velocity, double Acceleration, double Deceleration, double Jerk, double KillDeceleration);

/// <summary>
/// Adaptive-profile ratios (ACS): every velocity set derives ACC = DEC = <see cref="AccPerVelocity"/> × VEL,
/// JERK = <see cref="JerkPerAcc"/> × ACC and KDEC = <see cref="KdecPerAcc"/> × ACC.
/// </summary>
public sealed record ProfileRatios(double AccPerVelocity, double JerkPerAcc, double KdecPerAcc)
{
    /// <summary>Largest accepted ratio — a sanity bound against a typo, not a physical limit.</summary>
    public const double MaxRatio = 1000.0;

    public static readonly ProfileRatios Default = new(10.0, 10.0, 2.0);

    /// <summary>Every ratio finite, &gt; 0 and ≤ <see cref="MaxRatio"/>.</summary>
    public bool IsValid()
        => Ok(AccPerVelocity) && Ok(JerkPerAcc) && Ok(KdecPerAcc);

    private static bool Ok(double r) => double.IsFinite(r) && r > 0 && r <= MaxRatio;
}

public record MotionDevice
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; } = 701;
    public string ControllerType { get; init; } = string.Empty; // "ACS" or "PI"
    public List<string> InstalledAxes { get; init; } = new();
    public bool Enabled { get; init; } = true;
    public Dictionary<string, PositionStruct> Positions { get; init; } = new();
}

public enum MotionError
{
    Success,
    DeviceNotFound,
    AxisError,
    HomingFailed,
    LimitReached,
    PositionNotFound,
    CommunicationError,
    Timeout,
    NotConnected,
    AlreadyMoving
}

public record MotionStatus
{
    public string DeviceName { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public bool Moving { get; init; }
    public PositionStruct CurrentPosition { get; init; } = new();
}
