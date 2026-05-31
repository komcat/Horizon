namespace Horizon.Workflow;

/// <summary>
/// Per-controller-type velocity limits (mm/s) for the move commands' optional
/// velocity override. The UI caps input to these; the workflow engine clamps to them
/// at run time as a safety net. A velocity of 0 means "leave the device's current
/// velocity unchanged" and is never clamped.
/// </summary>
public static class MotionVelocityLimits
{
    /// <summary>(min, max) mm/s for a controller type — PI (hexapod) is the tighter range.</summary>
    public static (double Min, double Max) For(string? controllerType)
        => string.Equals(controllerType, "PI", StringComparison.OrdinalIgnoreCase)
            ? (0.01, 10.0)
            : (0.1, 50.0);

    /// <summary>Clamps a non-zero velocity to the controller's range; 0 (use current) passes through.</summary>
    public static double Clamp(double velocity, string? controllerType)
    {
        if (velocity <= 0) return velocity;
        var (min, max) = For(controllerType);
        return Math.Clamp(velocity, min, max);
    }
}
