using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.UCorrection;

/// <summary>
/// Inputs for a U-correction run. The four reference points are <b>named
/// positions in the active product</b> (taught beforehand): the PIC line is
/// <see cref="PicPoint1"/>→<see cref="PicPoint2"/>, the FAU line is
/// <see cref="FauPoint1"/>→<see cref="FauPoint2"/>. All four live on the
/// <see cref="GantryDevice"/>; the correction is a relative move on the
/// <see cref="HexapodDevice"/>'s U axis.
/// </summary>
public sealed record UCorrectionOptions(
    string GantryDevice,
    string HexapodDevice,
    string PicPoint1,
    string PicPoint2,
    string FauPoint1,
    string FauPoint2,
    // Slow PI system velocity (mm/s) used while applying the U move; restored afterwards.
    double ApplyVelocityMmPerSec = 0.1,
    // Refuse a navigation move whose saved target is farther than this from the current gantry position (stale-file guard).
    double MaxMoveDistanceMm = 20.0,
    // Sign of the applied U rotation; flip to -1 if the hexapod U convention is reversed.
    double Sign = +1.0,
    // Hard guard (deg): refuse to apply a U rotation larger than this — a bigger value signals a bad capture set.
    double MaxUvwDeg = 2.0,
    // Camera to re-expose at each stop while travelling; null leaves each position's own camera/exposure in effect.
    string? Camera = null,
    // Exposure (µs) for the PIC points when Camera is set.
    double? PicExposureUs = 400.0,
    // Exposure (µs) for the FAU points when Camera is set.
    double? FauExposureUs = 1500.0,
    // Write the operator-refined captures back over the four named positions, so they become the next run's targets.
    bool PersistRefinedPoints = true);

/// <summary>Outcome of a U-correction run.</summary>
/// <param name="AngleDegrees">The raw measured PIC→FAU angle.</param>
/// <param name="CorrectionDegrees">The U rotation actually requested (<c>Sign × angle</c>).</param>
/// <param name="Applied">True if the hexapod U move was issued; false if the operator declined at the apply confirmation.</param>
public sealed record UCorrectionResult(double AngleDegrees, double CorrectionDegrees, bool Applied);

/// <summary>
/// Headless U-correction teach + apply, Layer 2. Drives the gantry through the
/// four reference points, asks the operator (via <see cref="IOperatorPrompt"/>)
/// to refine the crosshair at each stop, captures the refined gantry position,
/// computes the PIC→FAU angle, and applies it as a slow relative U move on the
/// hexapod.
///
/// No camera/pixel dependency: the operator-refinement step is an
/// <see cref="IOperatorPrompt.ConfirmAsync"/> round-trip, so a console host
/// (stdin) drives it blind and a FAAM.UI head supplies a live camera view —
/// same routine either way. Mirrors the dependency-injection shape of
/// <c>WorkflowRunner</c>: the orchestrator owns no stores, it is handed
/// position read/save + move delegates by <see cref="ProcessService"/>.
/// </summary>
public sealed class UCorrectionRoutine
{
    /// <summary>Recall a named position with move overrides (the <see cref="ProcessService"/> recall path).</summary>
    public delegate Task<Result> MoveToNamedAsync(string device, string name, MoveOverrides overrides, CancellationToken ct);

    /// <summary>Persist a named position (the <c>motion.updatePosition</c> back-end).</summary>
    public delegate Result SaveNamedPosition(string device, string name, MotionPosition coordinates, double velocity);

    private readonly IHorizonEngine _engine;
    private readonly MoveToNamedAsync _moveToNamed;
    private readonly Func<string, string, NamedPosition?> _getPosition;
    private readonly SaveNamedPosition _savePosition;
    private readonly IOperatorPrompt? _prompt;
    private readonly Action<LogLevel, string> _log;

    public UCorrectionRoutine(
        IHorizonEngine engine,
        MoveToNamedAsync moveToNamed,
        Func<string, string, NamedPosition?> getPosition,
        SaveNamedPosition savePosition,
        IOperatorPrompt? prompt,
        Action<LogLevel, string> log)
    {
        _engine = engine;
        _moveToNamed = moveToNamed;
        _getPosition = getPosition;
        _savePosition = savePosition;
        _prompt = prompt;
        _log = log;
    }

    public async Task<Result<UCorrectionResult>> RunAsync(UCorrectionOptions o, CancellationToken ct = default)
    {
        if (_prompt == null)
            return Fail("U correction needs an operator prompt to refine each point — none registered.");

        // The four points in capture order, each tagged with its exposure preset.
        var points = new[]
        {
            (Name: o.PicPoint1, Exposure: o.PicExposureUs, Label: "PIC P1"),
            (Name: o.PicPoint2, Exposure: o.PicExposureUs, Label: "PIC P2"),
            (Name: o.FauPoint1, Exposure: o.FauExposureUs, Label: "FAU F1"),
            (Name: o.FauPoint2, Exposure: o.FauExposureUs, Label: "FAU F2"),
        };

        // Up-front: all four must be taught, so we never strand the operator mid-run.
        var missing = points.Where(p => _getPosition(o.GantryDevice, p.Name) == null)
                            .Select(p => p.Name).ToList();
        if (missing.Count > 0)
            return Fail($"Positions not taught on '{o.GantryDevice}': {string.Join(", ", missing)}.");

        var captured = new MotionPosition[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (name, exposure, label) = points[i];

            // 1) Navigate to the saved point (camera/exposure applied during travel).
            var distOk = await CheckMoveDistanceAsync(o, name, ct).ConfigureAwait(false);
            if (!distOk.IsSuccess) return Fail(distOk.Message);

            _log(LogLevel.Info, $"U correction → moving to {label} ('{name}').");
            var overrides = string.IsNullOrWhiteSpace(o.Camera)
                ? new MoveOverrides()
                : new MoveOverrides(Camera: o.Camera, ExposureUs: exposure);
            var move = await _moveToNamed(o.GantryDevice, name, overrides, ct).ConfigureAwait(false);
            if (!move.IsSuccess) return Fail($"Move to {label}: {move.Message}");

            // 2) Operator refines the crosshair, then confirms (Cancel aborts the run).
            bool go = await _prompt.ConfirmAsync(
                "U correction",
                $"Center the crosshair on {label}, then confirm. Cancel to abort.",
                ct).ConfigureAwait(false);
            if (!go) return Fail($"Operator aborted at {label}.");

            // 3) Capture the *refined* current gantry position.
            var read = await _engine.Motion.GetPositionAsync(o.GantryDevice).ConfigureAwait(false);
            if (!read.IsSuccess) return Fail($"Read position at {label}: {read.Message}");
            captured[i] = read.Value;
            _log(LogLevel.Info, $"Captured {label}: X={read.Value.X:F3}, Y={read.Value.Y:F3}, Z={read.Value.Z:F3}");
        }

        // 4) Compute the PIC→FAU angle and the signed correction.
        double angle = UCorrectionAngleCalculator.ComputeAngleDegrees(
            captured[0], captured[1], captured[2], captured[3]);
        double correction = o.Sign * angle;
        _log(LogLevel.Info, $"Angle PIC→FAU = {angle:F4}° → U correction {(correction >= 0 ? "+" : "")}{correction:F4}°");

        // 4b) Hexapod UVW safety guard — a correction beyond ±MaxUvwDeg means a bad
        //     capture set; refuse rather than command a large unintended rotation.
        if (Math.Abs(correction) > o.MaxUvwDeg)
            return Fail($"U correction {correction:F4}° exceeds the ±{o.MaxUvwDeg:0.##}° hexapod guard — re-check the captured points.");

        // 5) Confirm the apply.
        bool apply = await _prompt.ConfirmAsync(
            "Apply U correction",
            $"Measured angle = {angle:F4}°.\nApply hexapod relative U = {correction:F4}°?",
            ct).ConfigureAwait(false);
        if (!apply)
        {
            _log(LogLevel.Warning, "Apply U correction declined by operator.");
            return Ok(new UCorrectionResult(angle, correction, Applied: false));
        }

        // 6) Persist the refined captures so they become the next run's targets.
        if (o.PersistRefinedPoints)
        {
            for (int i = 0; i < points.Length; i++)
            {
                var stored = _getPosition(o.GantryDevice, points[i].Name);
                _savePosition(o.GantryDevice, points[i].Name, captured[i], stored?.Velocity ?? 0);
            }
            _log(LogLevel.Info, "Saved refined captures over the four reference positions.");
        }

        // 7) Apply the relative U move at slow system velocity, restoring it after.
        var applied = await ApplyUAtSlowVelocityAsync(o, correction, ct).ConfigureAwait(false);
        if (!applied.IsSuccess) return Fail($"Apply U: {applied.Message}");

        _log(LogLevel.Info, $"Applied hexapod U = {correction:F4}°.");
        return Ok(new UCorrectionResult(angle, correction, Applied: true));
    }

    /// <summary>
    /// Issues a relative U move on the hexapod at <see cref="UCorrectionOptions.ApplyVelocityMmPerSec"/>,
    /// restoring the prior system velocity afterwards. Mirrors the legacy
    /// <c>MoveHexapodRelativeAtSlowVelocity</c>; velocity changes are best-effort
    /// (logged, never fatal) so a controller without a readable system velocity
    /// still gets the move.
    /// </summary>
    private async Task<Result> ApplyUAtSlowVelocityAsync(UCorrectionOptions o, double correctionDeg, CancellationToken ct)
    {
        var prior = await _engine.Motion.GetSystemVelocityAsync(o.HexapodDevice).ConfigureAwait(false);
        double slow = MotionVelocityLimits.Clamp(o.ApplyVelocityMmPerSec, _engine.Motion.GetDevice(o.HexapodDevice)?.ControllerType);

        var set = await _engine.Motion.SetDeviceVelocityAsync(o.HexapodDevice, slow).ConfigureAwait(false);
        if (!set.IsSuccess) _log(LogLevel.Warning, $"Slow velocity not set on '{o.HexapodDevice}': {set.Message}");

        try
        {
            return await _engine.Motion.MoveRelativeAsync(o.HexapodDevice, "U", correctionDeg, ct).ConfigureAwait(false);
        }
        finally
        {
            if (prior.IsSuccess)
                await _engine.Motion.SetDeviceVelocityAsync(o.HexapodDevice, prior.Value).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stale-file guard: refuses a navigation move when the saved target is more
    /// than <see cref="UCorrectionOptions.MaxMoveDistanceMm"/> from the current
    /// gantry position (XYZ). Passes when the current position can't be read.
    /// </summary>
    private async Task<Result> CheckMoveDistanceAsync(UCorrectionOptions o, string name, CancellationToken ct)
    {
        var target = _getPosition(o.GantryDevice, name);
        if (target == null) return Result.Fail(ErrorCode.FileNotFound, $"Position '{name}' not found.");

        var cur = await _engine.Motion.GetPositionAsync(o.GantryDevice).ConfigureAwait(false);
        if (!cur.IsSuccess) return Result.Ok();   // can't read — let the move proceed

        double dx = target.Coordinates.X - cur.Value.X;
        double dy = target.Coordinates.Y - cur.Value.Y;
        double dz = target.Coordinates.Z - cur.Value.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return dist <= o.MaxMoveDistanceMm
            ? Result.Ok()
            : Result.Fail(ErrorCode.OperationFailed,
                $"Move to '{name}' blocked: {dist:F2} mm > {o.MaxMoveDistanceMm:F0} mm (stale position file?).");
    }

    private Result<UCorrectionResult> Ok(UCorrectionResult r) => Result<UCorrectionResult>.Ok(r);

    private Result<UCorrectionResult> Fail(string message)
    {
        _log(LogLevel.Error, $"U correction: {message}");
        return Result<UCorrectionResult>.Fail(ErrorCode.OperationFailed, message);
    }
}
