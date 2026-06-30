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

    /// <summary>IO device carrying the dispenser shot output pin.</summary>
    public string ShotIoDevice { get; init; } = "IOBottom";

    /// <summary>Output pin pulsed to fire the epoxy shot at needle touch.</summary>
    public string ShotPin { get; init; } = "Dispenser_Shot";

    /// <summary>Settle delay after toggling the shot output, applied on both the ON edge
    /// (before the dwell) and the OFF edge (before lift-off) (ms).</summary>
    public int OutputDebounceMs { get; init; } = 300;

    /// <summary>Epoxy dispense dwell while the shot output is ON and the needle touches (ms).</summary>
    public int DwellMs { get; init; } = 500;

    /// <summary>Manual touch-height tweak applied to the auto-computed touch Z (mm):
    /// + presses deeper toward the surface, − backs the needle off higher.</summary>
    public double TouchZAdjustMm { get; init; }

    /// <summary>Slow lift-off distance after dispensing (mm); direction is inferred toward focus.</summary>
    public double LiftoffMm { get; init; } = 5.0;

    /// <summary>Slow-approach distance before the needle-touch height on automated re-runs (mm):
    /// the gantry fast-moves to this far above the known touch Z, then crawls the rest at
    /// <see cref="SlowZVelocity"/>. Mirrors <see cref="LiftoffMm"/>.</summary>
    public double ApproachMm { get; init; } = 10.0;

    /// <summary>Velocity for the slow needle-touch approach / lift-off Z moves (mm/s).</summary>
    public double SlowZVelocity { get; init; } = 1.0;

    /// <summary>Taught cal-surface focus location; null until the first run teaches it.</summary>
    public CalPoint? CalPosition { get; init; }

    // --- results (written by the routine) ---

    /// <summary>Camera→needle XY offset (mm): the relative gantry move that puts the
    /// needle under the point the camera is centered on (needle = cam + offset).
    /// Seeded with the measured mount default; overwritten by each calibration run.</summary>
    public double VcamneedleX { get; init; } = -102.5;
    public double VcamneedleY { get; init; } = 13.56;

    /// <summary>Focus height minus needle-touch height (mm): how far below camera focus the needle tip sits.</summary>
    public double FocusToNeedleZmm { get; init; }

    public string? SavedBy { get; init; }
    public string? SavedAt { get; init; }
}

/// <summary>Inputs handed to one calibration run (built from the stored config).</summary>
internal sealed record CameraNeedleOptions(
    string GantryDevice,
    string DispenserSlide,
    string ShotIoDevice,
    string ShotPin,
    int OutputDebounceMs,
    int DwellMs,
    double LiftoffMm,
    double ApproachMm,
    double SlowZVelocity,
    double TouchZAdjustMm,
    CalPoint? CalPosition,
    double VcamneedleX,
    double VcamneedleY,
    double FocusToNeedleZmm);

/// <summary>Outcome of one calibration run — the values the service persists.</summary>
public sealed record CameraNeedleResult(
    double VcamneedleX,
    double VcamneedleY,
    double FocusToNeedleZmm,
    CalPoint CalPosition);
