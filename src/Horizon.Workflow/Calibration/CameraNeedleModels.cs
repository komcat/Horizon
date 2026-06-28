namespace Horizon.Workflow.Calibration;

/// <summary>A taught XYZ point (the flat calibration-surface focus location).</summary>
public sealed record CalPoint(double X, double Y, double Z);

/// <summary>
/// Persisted camera→needle calibration — machine-global (the camera/needle mount
/// is the same regardless of product). Holds both the configurable inputs and the
/// measured results, in <c>%APPDATA%\Horizon\config\camera_needle_calibration.json</c>.
/// </summary>
public sealed record CameraNeedleCalibrationData
{
    // --- configuration (edit in the JSON to match the rig) ---

    /// <summary>The gantry carrying the camera + dispenser.</summary>
    public string GantryDevice { get; init; } = "gantry-main";

    /// <summary>Pneumatic slide that lowers/raises the dispenser head (sensor-confirmed).</summary>
    public string DispenserSlide { get; init; } = "Dispenser_Head";

    /// <summary>Epoxy dispense dwell while the head is extended and the needle touches (ms).</summary>
    public int DwellMs { get; init; } = 300;

    /// <summary>Slow lift-off distance after dispensing (mm); direction is inferred toward focus.</summary>
    public double LiftoffMm { get; init; } = 5.0;

    /// <summary>Velocity for the needle-touch lift-off Z move (mm/s).</summary>
    public double SlowZVelocity { get; init; } = 1.0;

    /// <summary>Taught cal-surface focus location; null until the first run teaches it.</summary>
    public CalPoint? CalPosition { get; init; }

    // --- results (written by the routine) ---

    /// <summary>Camera→needle XY offset (mm): the relative gantry move that puts the
    /// needle under the point the camera is centered on.</summary>
    public double VcamneedleX { get; init; }
    public double VcamneedleY { get; init; }

    /// <summary>Focus height minus needle-touch height (mm): how far below camera focus the needle tip sits.</summary>
    public double FocusToNeedleZmm { get; init; }

    public string? SavedBy { get; init; }
    public string? SavedAt { get; init; }
}

/// <summary>Inputs handed to one calibration run (built from the stored config).</summary>
internal sealed record CameraNeedleOptions(
    string GantryDevice,
    string DispenserSlide,
    int DwellMs,
    double LiftoffMm,
    double SlowZVelocity,
    CalPoint? CalPosition,
    double VcamneedleX,
    double VcamneedleY);

/// <summary>Outcome of one calibration run — the values the service persists.</summary>
public sealed record CameraNeedleResult(
    double VcamneedleX,
    double VcamneedleY,
    double FocusToNeedleZmm,
    CalPoint CalPosition);
