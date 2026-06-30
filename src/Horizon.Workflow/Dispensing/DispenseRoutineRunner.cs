using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Dispensing;

/// <summary>
/// Runs a saved <see cref="DispenseRoutine"/> at the operator's current camera target.
/// The camera→needle calibration supplies the cam→needle XY offset and the focus→touch Z,
/// so "point the camera, dispense" works: the anchor is wherever the gantry is now (camera
/// centred on the target); the needle base = anchor.XY + offset; the touch height is derived
/// from the current Z as the focus reference. The pattern then expands to one or more dots,
/// each laid down with the shared <see cref="DispenseHead"/> shot primitive.
/// </summary>
internal sealed class DispenseRoutineRunner
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;
    private readonly DispenseHead _head;

    public DispenseRoutineRunner(IHorizonEngine engine, Action<LogLevel, string> log)
    {
        _engine = engine;
        _log = log;
        _head = new DispenseHead(engine, log, "Dispense");
    }

    /// <param name="vcamneedleX">Camera→needle X offset (mm).</param>
    /// <param name="vcamneedleY">Camera→needle Y offset (mm).</param>
    /// <param name="focusToNeedleZmm">Focus height minus needle-touch height (mm); must be non-zero
    /// (the camera→needle calibration must have been run).</param>
    public async Task<Result> RunAsync(
        DispenseRoutine routine, double vcamneedleX, double vcamneedleY, double focusToNeedleZmm,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(routine.GantryDevice))
            return Result.Fail(ErrorCode.InvalidParameter, "Dispense routine has no gantry device.");
        if (Math.Abs(focusToNeedleZmm) < 1e-6)
            return Result.Fail(ErrorCode.OperationFailed,
                "Camera→needle calibration not done (focus→needle Z is zero). Run the calibration first.");

        var g = routine.GantryDevice;
        var shot = routine.Shot;
        var points = routine.Pattern.Expand();

        try
        {
            // Anchor = current camera pose. Needle base = anchor.XY + offset; focus Z = anchor.Z.
            var anchorRes = await _engine.Motion.GetPositionAsync(g).ConfigureAwait(false);
            if (!anchorRes.IsSuccess) return Result.Fail(anchorRes.Error, $"Read camera anchor: {anchorRes.Message}");
            var anchor = anchorRes.Value;
            double baseX = anchor.X + vcamneedleX;
            double baseY = anchor.Y + vcamneedleY;
            double focusZ = anchor.Z;

            _log(LogLevel.Info,
                $"Dispense '{routine.Name}': {points.Count} dot(s), pattern {routine.Pattern.Kind}, " +
                $"anchor=({anchor.X:F3}, {anchor.Y:F3}), needle base=({baseX:F3}, {baseY:F3}), focus Z={focusZ:F3}.");

            // Flat surface -> the touch height is the same for every dot, and so is the approach
            // level just above it. Between dots the needle stays at the approach level (no full Z
            // stroke back to focus): traverse XY there, then slow-descend the short approach gap.
            double touchZ = DispenseHead.ComputeTouchZ(focusZ, focusToNeedleZmm, shot.TouchZAdjustMm, out double downSign);
            double approach = Math.Abs(shot.ApproachMm);
            double approachZ = touchZ - downSign * approach;

            // Move the needle over the FIRST dot at focus height with the head still UP, so the
            // long camera→needle traverse happens clear of the surface; only then lower the head.
            var firstXy = await MoveXyAsync(g, baseX + points[0].Dx, baseY + points[0].Dy, ct).ConfigureAwait(false);
            if (!firstXy.IsSuccess) return await AbortAsync(routine, shot, $"Move to dispense XY: {firstXy.Message}", firstXy.Error);

            var ext = await _head.ExtendAsync(routine.DispenserSlide, ct).ConfigureAwait(false);
            if (!ext.IsSuccess) { await _head.RetractAsync(routine.DispenserSlide, CancellationToken.None).ConfigureAwait(false); return ext; }

            // Fast pre-position to the approach level above the surface (head stays down for the run).
            var preZ = await _engine.Motion.MoveAbsoluteAsync(g, "Z", approachZ, ct).ConfigureAwait(false);
            if (!preZ.IsSuccess) return await AbortAsync(routine, shot, $"Approach pre-position: {preZ.Message}", preZ.Error);

            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];

                // Dots after the first: traverse to the next dot AT the approach level (no full lift).
                if (i > 0)
                {
                    var xy = await MoveXyAsync(g, baseX + p.Dx, baseY + p.Dy, ct).ConfigureAwait(false);
                    if (!xy.IsSuccess) return await AbortAsync(routine, shot, $"Move (dot {i + 1}): {xy.Message}", xy.Error);
                }

                // Slow-descend the approach gap to touch, fire, slow detach lift-off.
                var down = await _head.SlowZMoveAsync(g, downSign * approach, shot.SlowZVelocity, ct).ConfigureAwait(false);
                if (!down.IsSuccess) return await AbortAsync(routine, shot, $"Descend (dot {i + 1}): {down.Message}", down.Error);

                var fire = await _head.FireShotAsync(shot.ShotIoDevice, shot.ShotPin, shot.OutputDebounceMs, shot.DwellMs, ct).ConfigureAwait(false);
                if (!fire.IsSuccess) return await AbortAsync(routine, shot, $"Shot (dot {i + 1}): {fire.Message}", fire.Error);

                var lift = await _head.SlowZMoveAsync(g, -downSign * Math.Abs(shot.LiftoffMm), shot.SlowZVelocity, ct).ConfigureAwait(false);
                if (!lift.IsSuccess) return await AbortAsync(routine, shot, $"Lift-off (dot {i + 1}): {lift.Message}", lift.Error);

                // Back up to the approach level for the next traverse (skip after the last dot).
                if (i < points.Count - 1)
                {
                    var up = await _engine.Motion.MoveAbsoluteAsync(g, "Z", approachZ, ct).ConfigureAwait(false);
                    if (!up.IsSuccess) return await AbortAsync(routine, shot, $"Re-approach (dot {i + 1}): {up.Message}", up.Error);
                }

                _log(LogLevel.Info, $"Dispense '{routine.Name}': dot {i + 1}/{points.Count} done.");
            }

            // Return to focus height and raise the head (once, at the end of the whole pattern).
            var zback = await _engine.Motion.MoveAbsoluteAsync(g, "Z", focusZ, ct).ConfigureAwait(false);
            if (!zback.IsSuccess) return await AbortAsync(routine, shot, $"Return to focus Z: {zback.Message}", zback.Error);
            var ret = await _head.RetractAsync(routine.DispenserSlide, ct).ConfigureAwait(false);
            if (!ret.IsSuccess) return ret;

            // Optional: bring the camera back over the last dispensed dot to inspect it. The
            // camera frames a point when the gantry sits at (point - vcamneedle) = anchor + offset.
            // Best-effort: a view-move hiccup doesn't fail an otherwise-successful dispense.
            if (routine.ReturnToView)
            {
                var last = points[points.Count - 1];
                var view = await MoveXyAsync(g, anchor.X + last.Dx, anchor.Y + last.Dy, ct).ConfigureAwait(false);
                _log(view.IsSuccess ? LogLevel.Info : LogLevel.Warning,
                    view.IsSuccess ? $"Dispense '{routine.Name}': camera returned to view the dot."
                                   : $"Dispense '{routine.Name}': return-to-view failed: {view.Message}");
            }

            _log(LogLevel.Info, $"Dispense '{routine.Name}' complete: {points.Count} dot(s).");
            return Result.Ok();
        }
        catch (OperationCanceledException)
        {
            await _head.FailSafeAsync(shot.ShotIoDevice, shot.ShotPin, routine.DispenserSlide).ConfigureAwait(false);
            return Result.Fail(ErrorCode.OperationFailed, $"Dispense '{routine.Name}' cancelled.");
        }
    }

    /// <summary>Moves the gantry to an absolute XY (X then Y), stopping on the first failure.</summary>
    private async Task<Result> MoveXyAsync(string gantry, double x, double y, CancellationToken ct)
    {
        var mx = await _engine.Motion.MoveAbsoluteAsync(gantry, "X", x, ct).ConfigureAwait(false);
        if (!mx.IsSuccess) return mx;
        return await _engine.Motion.MoveAbsoluteAsync(gantry, "Y", y, ct).ConfigureAwait(false);
    }

    /// <summary>Make safe (shot off + retract) then return the failing result.</summary>
    private async Task<Result> AbortAsync(DispenseRoutine routine, DispenseShot shot, string message, ErrorCode code)
    {
        await _head.FailSafeAsync(shot.ShotIoDevice, shot.ShotPin, routine.DispenserSlide).ConfigureAwait(false);
        return Result.Fail(code, message);
    }
}
