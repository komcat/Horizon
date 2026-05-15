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

/// <summary>Cached motion state for one device. <see cref="Valid"/> is false until first poll.</summary>
public sealed record MotionDeviceState(bool Valid, bool IsMoving, MotionPosition Position)
{
    public static readonly MotionDeviceState Unknown = new(false, false, MotionPosition.Zero);
}

/// <summary>Raised on <see cref="IMotionFacet.StateChanged"/> when a device's polled state changes.</summary>
public sealed record MotionStateChanged(string Device, MotionDeviceState State);
