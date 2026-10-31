using System.Text.Json.Serialization;

namespace Horizon.Workflow.Calibration;

/// <summary>A gantry pose (raw axis mm) recorded by the calibration.</summary>
public sealed record CalPoint(double X, double Y, double Z);

/// <summary>
/// One "clear first" move: before the calibration's first prompt, <see cref="Device"/> goes to
/// its saved position named <see cref="Position"/> (the hexapods out of the camera's way). Held
/// by NAME and resolved against the active product at Run — a missing name refuses the run
/// before anything moves.
/// </summary>
public sealed record NeedleClearMove(string Device, string Position);

/// <summary>
/// The machine-global camera→needle calibration — the dispense needle's lever arm, ported from
/// FireDog's <c>needle_cal.json</c> + <c>needle_calibration.lua</c>
/// (<c>%APPDATA%\Horizon\config\camera_needle_calibration.json</c>). Holds the hardware, the
/// protocol parameters, and the measured result with the evidence behind it.
///
/// <para><b>Machine-level, not per-product</b>: the needle is a fixture; it is re-taught when the
/// needle changes, not when the product does. <b>Axis space, never global</b>: camera and needle
/// ride the same device, so every number here is a raw axis coordinate of
/// <see cref="GantryDevice"/>. <b>The Z direction is never assumed</b>: which way is "down" is the
/// sign of the measured <see cref="Dz"/>.</para>
/// </summary>
public sealed record CameraNeedleCalibrationData
{
    // --- hardware -------------------------------------------------------------------------

    /// <summary>The gantry carrying BOTH the camera and the needle.</summary>
    public string GantryDevice { get; init; } = "gantry-main";

    /// <summary>Pneumatic slide that puts the needle down / up (sensor-confirmed).</summary>
    public string DispenserSlide { get; init; } = "Dispenser_Head";

    /// <summary>IO device carrying the glue-valve output.</summary>
    public string ShotIoDevice { get; init; } = "IOBottom";

    /// <summary>The glue-valve output. Open time is the shot size.</summary>
    public string ShotPin { get; init; } = "Dispenser_Shot";

    // --- protocol parameters (FireDog ndl_*) -----------------------------------------------

    /// <summary>Valve open time for the imprint (ms).</summary>
    public int ShotMs { get; init; } = 300;

    /// <summary>Pause after the slide extends, before Z moves (ms).</summary>
    public int SettleMs { get; init; } = 500;

    /// <summary>Safety height: on a verify pass the fast approach stops this far above the verify gap (mm).</summary>
    public double ClearMm { get; init; } = 1.0;

    /// <summary>Pass 1 parks this far above the PREVIOUS touch height (mm) — wider than the safety
    /// height because the usual reason to calibrate is a changed needle, which may sit lower.</summary>
    public double FirstClearMm { get; init; } = 3.0;

    /// <summary>A verify pass stops this far above the predicted touch (mm); the operator closes it.</summary>
    public double VerifyGapMm { get; init; } = 0.1;

    /// <summary>After the shot Z lifts this far off the dot before the slide comes up (mm).</summary>
    public double LiftMm { get; init; } = 1.5;

    /// <summary>1 measures once (nothing checks it); 2+ verifies.</summary>
    public int Passes { get; init; } = 2;

    /// <summary>XY residual a verify pass must hit (mm).</summary>
    public double TolMm { get; init; } = 0.05;

    /// <summary>Z residual a verify pass must hit (mm).</summary>
    public double ZTolMm { get; init; } = 0.05;

    /// <summary>XY traverse: needle over the spot, camera back over the dot (mm/s).</summary>
    public double VTraverse { get; init; } = 40.0;

    /// <summary>Z down to the safety height (mm/s).</summary>
    public double VApproach { get; init; } = 20.0;

    /// <summary>Z from the safety height to the verify gap (mm/s).</summary>
    public double VFinal { get; init; } = 5.0;

    /// <summary>Z lifting off the dot after the shot (mm/s).</summary>
    public double VLiftoff { get; init; } = 5.0;

    /// <summary>Devices moved to a saved position, in order, before the first prompt.</summary>
    public IReadOnlyList<NeedleClearMove> ClearMoves { get; init; } = Array.Empty<NeedleClearMove>();

    // --- result -----------------------------------------------------------------------------

    /// <summary>Camera→needle X (mm): add to a CAMERA pose to get the pose that puts the NEEDLE there.</summary>
    public double VcamneedleX { get; init; } = -102.5;

    /// <summary>Camera→needle Y (mm).</summary>
    public double VcamneedleY { get; init; } = 13.56;

    /// <summary>Focused camera Z minus needle-touch Z (mm) — the stored form of <see cref="Dz"/>, negated.</summary>
    public double FocusToNeedleZmm { get; init; }

    /// <summary>True once a calibration has been measured; false for a typed seed (enough to AIM the
    /// first imprint, never enough to dispense through). Null in a file written before this field
    /// existed — then a non-zero <see cref="FocusToNeedleZmm"/> counts as measured.</summary>
    public bool? Valid { get; init; }

    /// <summary>A later pass agreed in XY AND in Z. Never true without <see cref="ZChecked"/>.</summary>
    public bool Verified { get; init; }

    /// <summary>A verify pass measured the Z half at all.</summary>
    public bool ZChecked { get; init; }

    /// <summary>How far the last verify pass moved the XY answer (mm).</summary>
    public double ResidualMm { get; init; }

    /// <summary>How far the real touch sat from the predicted one, signed along "down" (mm; + = touched lower).</summary>
    public double ZResidualMm { get; init; }

    /// <summary>The camera, focused on the coupon — the pose the offset is subtracted from.</summary>
    public CalPoint? Camera { get; init; }

    /// <summary>The gantry when the needle touched and imprinted.</summary>
    public CalPoint? Needle { get; init; }

    /// <summary>The camera pose centred on the imprint (Z unused).</summary>
    public CalPoint? Imprint { get; init; }

    public string? CalibratedBy { get; init; }
    public string? CalibratedAt { get; init; }

    public string? SavedBy { get; init; }
    public string? SavedAt { get; init; }

    /// <summary>A measured calibration exists (a seed does not count).</summary>
    [JsonIgnore]
    public bool IsCalibrated => Valid ?? Math.Abs(FocusToNeedleZmm) > 1e-6;

    /// <summary>Add to a FOCUSED camera Z to reach the needle-touch Z (mm, axis space).</summary>
    [JsonIgnore]
    public double Dz => -FocusToNeedleZmm;

    /// <summary>+1 / −1: which way the Z axis moves to take the needle DOWN — from the measurement.</summary>
    [JsonIgnore]
    public double DownSign => Dz >= 0 ? 1.0 : -1.0;
}

/// <summary>Outcome of one calibration pass — what the service persists after every pass.</summary>
public sealed record CameraNeedleResult(
    double VcamneedleX,
    double VcamneedleY,
    double FocusToNeedleZmm,
    CalPoint Camera,
    CalPoint Needle,
    CalPoint Imprint,
    int Pass,
    bool ZChecked,
    double ResidualMm,
    double ZResidualMm,
    bool Verified);
