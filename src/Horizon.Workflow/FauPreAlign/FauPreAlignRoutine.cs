using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.FauPreAlign;

/// <summary>
/// Inputs for a FAU pre-align run. The four reference points are named positions in
/// the active product (taught beforehand) on the <see cref="GantryDevice"/>: the
/// output/PIC pair is <see cref="Out1"/>/<see cref="Out2"/>, the FAU fiber pair is
/// <see cref="Fiber1"/>/<see cref="Fiber2"/>. The computed correction is applied as a
/// relative Y/Z move on the <see cref="HexapodDevice"/>.
/// </summary>
public sealed record FauPreAlignOptions(
    string GantryDevice,
    string HexapodDevice,
    string Out1,
    string Out2,
    string Fiber1,
    string Fiber2,
    // Desired fiber→output offset: distance (µm) at an angle (deg) — the "master" vector.
    double TargetDistanceUm = 100.0,
    double TargetAngleDeg = 0.0,
    // Slow PI system velocity (mm/s) used while applying the correction; restored afterwards.
    double ApplyVelocityMmPerSec = 0.1,
    // Refuse a navigation move whose saved target is farther than this from the current gantry position (stale-file guard).
    double MaxMoveDistanceMm = 20.0,
    // Camera to re-expose at each stop while travelling; null leaves each position's own camera/exposure in effect.
    string? Camera = null,
    // Exposure (µs) for the output points when Camera is set.
    double? OutExposureUs = 400.0,
    // Exposure (µs) for the fiber points when Camera is set.
    double? FiberExposureUs = 1500.0,
    // Write the operator-refined captures back over the four named positions, so they become the next run's targets.
    bool PersistRefinedPoints = true);

/// <summary>Outcome of a FAU pre-align run.</summary>
/// <param name="CorrectionXmm">Gantry-XY correction X (mm).</param>
/// <param name="CorrectionYmm">Gantry-XY correction Y (mm).</param>
/// <param name="HexYmm">Hexapod-local Y move applied (mm).</param>
/// <param name="HexZmm">Hexapod-local Z move applied (mm).</param>
/// <param name="Applied">True if the hexapod move was issued; false if the operator declined.</param>
public sealed record FauPreAlignResult(
    double CorrectionXmm, double CorrectionYmm, double HexYmm, double HexZmm, bool Applied);

/// <summary>
/// Headless FAU pre-align teach + apply, Layer 2. Drives the gantry through the four
/// reference points (Out1·Out2·Fiber1·Fiber2), asks the operator (via
/// <see cref="IOperatorPrompt"/>) to refine the crosshair at each stop, captures the
/// refined gantry position, computes the gantry-XY correction toward the master
/// vector, maps it to a hexapod-local Y/Z move, and applies it slowly.
///
/// Same shape as <c>UCorrectionRoutine</c>: no stores of its own — position
/// read/save + recall-move are injected by <see cref="ProcessService"/>; the
/// per-point refine is an <see cref="IOperatorPrompt.ConfirmAsync"/> round-trip so a
/// console host or a non-modal Run-page panel can both drive it.
/// </summary>
public sealed class FauPreAlignRoutine
{
    /// <summary>Recall a named position with move overrides (the <see cref="ProcessService"/> recall path).</summary>
    public delegate Task<Result> MoveToNamedAsync(string device, string name, MoveOverrides overrides, CancellationToken ct);

    /// <summary>Persist a named position.</summary>
    public delegate Result SaveNamedPosition(string device, string name, MotionPosition coordinates, double velocity);

    private readonly IHorizonEngine _engine;
    private readonly MoveToNamedAsync _moveToNamed;
    private readonly Func<string, string, NamedPosition?> _getPosition;
    private readonly SaveNamedPosition _savePosition;
    private readonly IOperatorPrompt? _prompt;
    private readonly Action<LogLevel, string> _log;

    public FauPreAlignRoutine(
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

    public async Task<Result<FauPreAlignResult>> RunAsync(FauPreAlignOptions o, CancellationToken ct = default)
    {
        if (_prompt == null)
            return Fail("FAU pre-align needs an operator prompt to refine each point — none registered.");

        var points = new[]
        {
            (Name: o.Out1, Exposure: o.OutExposureUs, Label: "Out1"),
            (Name: o.Out2, Exposure: o.OutExposureUs, Label: "Out2"),
            (Name: o.Fiber1, Exposure: o.FiberExposureUs, Label: "Fiber1"),
            (Name: o.Fiber2, Exposure: o.FiberExposureUs, Label: "Fiber2"),
        };

        var missing = points.Where(p => _getPosition(o.GantryDevice, p.Name) == null)
                            .Select(p => p.Name).ToList();
        if (missing.Count > 0)
            return Fail($"Positions not taught on '{o.GantryDevice}': {string.Join(", ", missing)}.");

        var captured = new MotionPosition[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (name, exposure, label) = points[i];

            var distOk = await CheckMoveDistanceAsync(o, name, ct).ConfigureAwait(false);
            if (!distOk.IsSuccess) return Fail(distOk.Message);

            _log(LogLevel.Info, $"FAU pre-align → moving to {label} ('{name}').");
            var overrides = string.IsNullOrWhiteSpace(o.Camera)
                ? new MoveOverrides()
                : new MoveOverrides(Camera: o.Camera, ExposureUs: exposure);
            var move = await _moveToNamed(o.GantryDevice, name, overrides, ct).ConfigureAwait(false);
            if (!move.IsSuccess) return Fail($"Move to {label}: {move.Message}");

            bool go = await _prompt.ConfirmAsync(
                "FAU pre-align",
                $"Center the crosshair on {label}, then confirm. Cancel to abort.",
                ct).ConfigureAwait(false);
            if (!go) return Fail($"Operator aborted at {label}.");

            var read = await _engine.Motion.GetPositionAsync(o.GantryDevice).ConfigureAwait(false);
            if (!read.IsSuccess) return Fail($"Read position at {label}: {read.Message}");
            captured[i] = read.Value;
            _log(LogLevel.Info, $"Captured {label}: X={read.Value.X:F3}, Y={read.Value.Y:F3}");
        }

        // Correction = master − (Xf − Xo), in gantry XY.
        var (masterX, masterY) = FauPreAlignVectorCalculator.MasterVectorMm(o.TargetDistanceUm, o.TargetAngleDeg);
        var (corrX, corrY) = FauPreAlignVectorCalculator.ComputeCorrectionXy(
            captured[0], captured[1], captured[2], captured[3], masterX, masterY);

        // Gantry → hex-right local axes (signs verified on hardware in the legacy):
        //   gantry +X → hex local Z−,  gantry +Y → hex local Y−.
        double hexY = -corrY;
        double hexZ = -corrX;
        _log(LogLevel.Info,
            $"Correction (gantry XY) = ({corrX:F4}, {corrY:F4}) mm → hex Y={hexY:F4}, Z={hexZ:F4} mm " +
            $"(target {o.TargetDistanceUm:0} µm @ {o.TargetAngleDeg:0.##}°)");

        bool apply = await _prompt.ConfirmAsync(
            "Apply FAU correction",
            $"Correction (gantry XY) = ({corrX:F4}, {corrY:F4}) mm.\n" +
            $"Apply hexapod move Y = {hexY:F4} mm, Z = {hexZ:F4} mm?",
            ct).ConfigureAwait(false);
        if (!apply)
        {
            _log(LogLevel.Warning, "FAU pre-align apply declined by operator.");
            return Ok(new FauPreAlignResult(corrX, corrY, hexY, hexZ, Applied: false));
        }

        if (o.PersistRefinedPoints)
        {
            for (int i = 0; i < points.Length; i++)
            {
                var stored = _getPosition(o.GantryDevice, points[i].Name);
                _savePosition(o.GantryDevice, points[i].Name, captured[i], stored?.Velocity ?? 0);
            }
            _log(LogLevel.Info, "Saved refined captures over the four reference positions.");
        }

        var applied = await ApplyHexMoveAtSlowVelocityAsync(o, hexY, hexZ, ct).ConfigureAwait(false);
        if (!applied.IsSuccess) return Fail($"Apply hex move: {applied.Message}");

        _log(LogLevel.Info, $"Applied hex move Y={hexY:F4}, Z={hexZ:F4} mm.");
        return Ok(new FauPreAlignResult(corrX, corrY, hexY, hexZ, Applied: true));
    }

    /// <summary>
    /// Applies the hexapod-local Y/Z correction as a single coordinated move at
    /// <see cref="FauPreAlignOptions.ApplyVelocityMmPerSec"/>, restoring the prior
    /// system velocity afterwards. Velocity changes are best-effort (logged, never
    /// fatal). The live pose is read and shifted on Y and Z together, so the hexapod
    /// travels both axes simultaneously (matching the legacy single combined move)
    /// rather than as two sequential single-axis steps.
    /// </summary>
    private async Task<Result> ApplyHexMoveAtSlowVelocityAsync(
        FauPreAlignOptions o, double hexY, double hexZ, CancellationToken ct)
    {
        var prior = await _engine.Motion.GetSystemVelocityAsync(o.HexapodDevice).ConfigureAwait(false);
        double slow = MotionVelocityLimits.Clamp(o.ApplyVelocityMmPerSec, _engine.Motion.GetDevice(o.HexapodDevice)?.ControllerType);

        var set = await _engine.Motion.SetDeviceVelocityAsync(o.HexapodDevice, slow).ConfigureAwait(false);
        if (!set.IsSuccess) _log(LogLevel.Warning, $"Slow velocity not set on '{o.HexapodDevice}': {set.Message}");

        try
        {
            // Read the live pose and shift Y+Z together → one coordinated multi-axis move.
            var pos = await _engine.Motion.GetPositionAsync(o.HexapodDevice).ConfigureAwait(false);
            if (!pos.IsSuccess)
                return Result.Fail(ErrorCode.OperationFailed, $"Read hexapod position: {pos.Message}");

            var target = pos.Value with { Y = pos.Value.Y + hexY, Z = pos.Value.Z + hexZ };
            return await _engine.Motion.MoveToPositionAsync(o.HexapodDevice, target, ct).ConfigureAwait(false);
        }
        finally
        {
            if (prior.IsSuccess)
                await _engine.Motion.SetDeviceVelocityAsync(o.HexapodDevice, prior.Value).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stale-file guard: refuses a navigation move when the saved target is more than
    /// <see cref="FauPreAlignOptions.MaxMoveDistanceMm"/> from the current gantry
    /// position (XYZ). Passes when the current position can't be read.
    /// </summary>
    private async Task<Result> CheckMoveDistanceAsync(FauPreAlignOptions o, string name, CancellationToken ct)
    {
        var target = _getPosition(o.GantryDevice, name);
        if (target == null) return Result.Fail(ErrorCode.FileNotFound, $"Position '{name}' not found.");

        var cur = await _engine.Motion.GetPositionAsync(o.GantryDevice).ConfigureAwait(false);
        if (!cur.IsSuccess) return Result.Ok();

        double dx = target.Coordinates.X - cur.Value.X;
        double dy = target.Coordinates.Y - cur.Value.Y;
        double dz = target.Coordinates.Z - cur.Value.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return dist <= o.MaxMoveDistanceMm
            ? Result.Ok()
            : Result.Fail(ErrorCode.OperationFailed,
                $"Move to '{name}' blocked: {dist:F2} mm > {o.MaxMoveDistanceMm:F0} mm (stale position file?).");
    }

    private Result<FauPreAlignResult> Ok(FauPreAlignResult r) => Result<FauPreAlignResult>.Ok(r);

    private Result<FauPreAlignResult> Fail(string message)
    {
        _log(LogLevel.Error, $"FAU pre-align: {message}");
        return Result<FauPreAlignResult>.Fail(ErrorCode.OperationFailed, message);
    }
}
