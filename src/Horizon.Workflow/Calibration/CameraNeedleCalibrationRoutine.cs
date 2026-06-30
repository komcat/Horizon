using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Calibration;

/// <summary>
/// Camera→needle calibration — a "dispense-and-look" routine that measures the
/// XY offset between the camera optical axis and the needle tip, plus the
/// focus-to-needle-tip Z. Modelled on <c>UCorrectionRoutine</c>: an algorithm
/// object driven by a Layer 3 panel, blocking on <see cref="IOperatorPrompt"/>
/// confirms while the operator jogs / MoveClicks against the live camera.
///
/// <para>The result is computed from two <b>absolute</b> gantry positions — the XY
/// where the dot was dispensed and the XY where the operator centred the camera on
/// that dot — so the apply-offset / move-back bookkeeping cancels out and there is
/// no sign or accumulation to get wrong:</para>
/// <code>
///   vcamneedle = G_dispense.xy - G_view.xy      // move that puts the needle under a camera target
///   focusToNeedleZ = z_focus - z_touch
/// </code>
/// </summary>
internal sealed class CameraNeedleCalibrationRoutine
{
    private readonly IHorizonEngine _engine;
    private readonly IOperatorPrompt? _prompt;
    private readonly Action<LogLevel, string> _log;

    public CameraNeedleCalibrationRoutine(
        IHorizonEngine engine, IOperatorPrompt? prompt, Action<LogLevel, string> log)
    {
        _engine = engine;
        _prompt = prompt;
        _log = log;
    }

    public async Task<Result<CameraNeedleResult>> RunAsync(CameraNeedleOptions o, CancellationToken ct = default)
    {
        if (_prompt == null)
            return Fail("No operator-prompt handler registered.", ErrorCode.NotSupported);

        var g = o.GantryDevice;
        try
        {
            // 1) Move to the taught cal position (skipped on the very first run).
            if (o.CalPosition is { } cp)
            {
                var mv = await _engine.Motion
                    .MoveToPositionAsync(g, new MotionPosition(cp.X, cp.Y, cp.Z, 0, 0, 0), ct)
                    .ConfigureAwait(false);
                if (!mv.IsSuccess) return Fail($"Move to cal position: {mv.Message}", mv.Error);
            }

            // 2) Operator focuses the camera Z on the flat surface -> capture P_focus.
            if (!await _prompt.ConfirmAsync("Camera focus",
                    "Jog Z to focus the camera on the flat calibration surface, then Confirm.", ct)
                    .ConfigureAwait(false))
                return Declined();
            var focus = await _engine.Motion.GetPositionAsync(g).ConfigureAwait(false);
            if (!focus.IsSuccess) return Fail($"Read focus position: {focus.Message}", focus.Error);
            var pFocus = focus.Value;

            // 3) Pre-apply the current offset so the dot lands in-frame on re-runs (0 first run).
            //    Purely ergonomic — the measurement below uses absolute positions.
            var pre = await ApplyRelativeXyAsync(g, o.VcamneedleX, o.VcamneedleY, ct).ConfigureAwait(false);
            if (!pre.IsSuccess) return Fail($"Pre-apply offset: {pre.Message}", pre.Error);

            // 4) Deposit a dot -> capture G_dispense (xy + z_touch).
            var disp = await DispenseAsync(o, pFocus.Z, ct).ConfigureAwait(false);
            if (!disp.IsSuccess) return Fail(disp.Message, disp.Error);
            var gDispense = disp.Value;

            // 5) Return to focus height (Z only) and move the offset back, so the camera frames the dot.
            var zback = await _engine.Motion.MoveAbsoluteAsync(g, "Z", pFocus.Z, ct).ConfigureAwait(false);
            if (!zback.IsSuccess) return Fail($"Return to focus Z: {zback.Message}", zback.Error);
            var back = await ApplyRelativeXyAsync(g, -o.VcamneedleX, -o.VcamneedleY, ct).ConfigureAwait(false);
            if (!back.IsSuccess) return Fail($"Move offset back: {back.Message}", back.Error);

            // 6) Operator centres the dot on the crosshair -> capture G_view.
            if (!await _prompt.ConfirmAsync("Center the dot",
                    "Jog / MoveClick the gantry until the epoxy dot sits on the center crosshair, then Confirm.", ct)
                    .ConfigureAwait(false))
                return Declined();
            var view = await _engine.Motion.GetPositionAsync(g).ConfigureAwait(false);
            if (!view.IsSuccess) return Fail($"Read centred position: {view.Message}", view.Error);
            var gView = view.Value;

            // 7) Compute directly from the two absolute positions.
            double vx = gDispense.X - gView.X;
            double vy = gDispense.Y - gView.Y;
            double focusToNeedleZ = pFocus.Z - gDispense.Z;

            _log(LogLevel.Info,
                $"Camera-needle: vcamneedle=({vx:F4}, {vy:F4}) mm, focus->needle Z={focusToNeedleZ:F4} mm.");
            return Result<CameraNeedleResult>.Ok(
                new CameraNeedleResult(vx, vy, focusToNeedleZ, new CalPoint(pFocus.X, pFocus.Y, pFocus.Z)));
        }
        catch (OperationCanceledException)
        {
            await FailSafeAsync(o).ConfigureAwait(false);
            return Fail("Calibration cancelled.", ErrorCode.OperationFailed);
        }
    }

    /// <summary>
    /// Deposits one epoxy dot and returns the gantry position where it was dispensed
    /// (its XY = dot location, its Z = needle-touch height). Isolated so the
    /// extend / touch / shot / dwell / lift-off ordering is easy to tune on the bench.
    /// The head extends, the needle reaches the surface, then the shot output fires:
    /// ON → debounce → dwell (epoxy window) → OFF → debounce → slow lift-off.
    /// </summary>
    private async Task<Result<MotionPosition>> DispenseAsync(
        CameraNeedleOptions o, double focusZ, CancellationToken ct)
    {
        var g = o.GantryDevice;

        // Lower the dispenser head (sensor-confirmed).
        _log(LogLevel.Info, $"Camera-needle: extending dispenser head '{o.DispenserSlide}'.");
        var ext = await _engine.Pneumatic.ExtendAndWaitAsync(o.DispenserSlide, ct).ConfigureAwait(false);
        if (!ext.IsSuccess)
            return Result<MotionPosition>.Fail(ext.Error, $"Dispenser extend: {ext.Message}");

        // Reach the needle-touch height. First-time (no prior Z relationship): the operator
        // jogs Z down to find it. On re-runs the touch height is known relative to focus, so
        // we OFFER an auto-descent (fast to ApproachMm above touch, then crawl at SlowZVelocity);
        // the operator can decline and jog manually instead.
        Result<MotionPosition> reach;
        if (Math.Abs(o.FocusToNeedleZmm) > 1e-6)
        {
            double touchZ = ComputeTouchZ(o, focusZ, out _);
            bool auto = await _prompt!.ConfirmAsync("Auto needle touch?",
                    $"Auto-descend to the known touch height (Z≈{touchZ:F3} mm)? " +
                    "Confirm = auto-descend, Cancel = jog manually.", ct).ConfigureAwait(false);
            if (auto)
            {
                reach = await AutoTouchAsync(o, focusZ, ct).ConfigureAwait(false);
            }
            else
            {
                // A real routine-cancel also returns false here — distinguish it from a
                // "no, I'll do it manually" by the token, and let cancel abort.
                ct.ThrowIfCancellationRequested();
                reach = await ManualTouchAsync(o, ct).ConfigureAwait(false);
            }
        }
        else
        {
            reach = await ManualTouchAsync(o, ct).ConfigureAwait(false);
        }
        if (!reach.IsSuccess) return reach;
        var gDispense = reach.Value;

        // Fire the shot: output ON -> debounce -> dwell (epoxy window) -> OFF -> debounce.
        // A finally guarantees the output goes OFF even on cancel / error mid-dwell.
        _log(LogLevel.Info,
            $"Camera-needle: shot ON {o.ShotIoDevice}/{o.ShotPin} (debounce {o.OutputDebounceMs} ms, dwell {o.DwellMs} ms).");
        var shotOn = await _engine.IO.SetOutputByNameAsync(o.ShotIoDevice, o.ShotPin, true, ct).ConfigureAwait(false);
        if (!shotOn.IsSuccess)
            return Result<MotionPosition>.Fail(shotOn.Error, $"Dispenser shot ON ({o.ShotIoDevice}/{o.ShotPin}): {shotOn.Message}");
        try
        {
            if (o.OutputDebounceMs > 0) await Task.Delay(o.OutputDebounceMs, ct).ConfigureAwait(false);
            if (o.DwellMs > 0) await Task.Delay(o.DwellMs, ct).ConfigureAwait(false);
        }
        finally
        {
            // Always de-energize; use None so an aborting token can't skip the OFF.
            var shotOff = await _engine.IO.SetOutputByNameAsync(o.ShotIoDevice, o.ShotPin, false, CancellationToken.None)
                .ConfigureAwait(false);
            _log(shotOff.IsSuccess ? LogLevel.Info : LogLevel.Warning,
                $"Camera-needle: shot OFF {o.ShotIoDevice}/{o.ShotPin}{(shotOff.IsSuccess ? "." : $" FAILED: {shotOff.Message}")}");
        }
        if (o.OutputDebounceMs > 0) await Task.Delay(o.OutputDebounceMs, ct).ConfigureAwait(false);

        // Slow lift-off, in the direction from touch toward focus (away from the surface).
        double upSign = Math.Sign(focusZ - gDispense.Z);
        if (upSign == 0) upSign = 1;
        var lift = await SlowZMoveAsync(g, upSign * Math.Abs(o.LiftoffMm), o.SlowZVelocity, ct).ConfigureAwait(false);
        if (!lift.IsSuccess) return Result<MotionPosition>.Fail(lift.Error, lift.Message);

        // Raise the dispenser head (sensor-confirmed).
        var ret = await _engine.Pneumatic.RetractAndWaitAsync(o.DispenserSlide, ct).ConfigureAwait(false);
        if (!ret.IsSuccess)
            return Result<MotionPosition>.Fail(ret.Error, $"Dispenser retract: {ret.Message}");

        return Result<MotionPosition>.Ok(gDispense);
    }

    /// <summary>
    /// First-time touch: the operator jogs Z down until the needle touches and confirms.
    /// Returns the gantry position at touch.
    /// </summary>
    private async Task<Result<MotionPosition>> ManualTouchAsync(CameraNeedleOptions o, CancellationToken ct)
    {
        if (!await _prompt!.ConfirmAsync("Needle touch",
                "Jog Z down until the needle just touches the surface, then Confirm.", ct).ConfigureAwait(false))
            return Result<MotionPosition>.Fail(ErrorCode.OperationFailed, "Operator declined at needle touch.");
        var pos = await _engine.Motion.GetPositionAsync(o.GantryDevice).ConfigureAwait(false);
        if (!pos.IsSuccess) return Result<MotionPosition>.Fail(pos.Error, $"Read dispense position: {pos.Message}");
        return Result<MotionPosition>.Ok(pos.Value);
    }

    /// <summary>
    /// Automated touch on re-runs: the touch height is known relative to focus
    /// (<c>touchZ = focusZ - FocusToNeedleZmm</c>). Fast-move to <see cref="CameraNeedleOptions.ApproachMm"/>
    /// above it, then crawl the rest at <see cref="CameraNeedleOptions.SlowZVelocity"/>. Returns the
    /// gantry position at touch.
    /// </summary>
    private async Task<Result<MotionPosition>> AutoTouchAsync(CameraNeedleOptions o, double focusZ, CancellationToken ct)
    {
        var g = o.GantryDevice;
        double touchZ = ComputeTouchZ(o, focusZ, out double downSign);
        double approach = Math.Abs(o.ApproachMm);
        double approachZ = touchZ - downSign * approach;   // ApproachMm short of touch, on the focus side

        _log(LogLevel.Info,
            $"Camera-needle: auto-approach to touch Z={touchZ:F4} (fast to {approachZ:F4}, slow {approach:F1} mm @ {o.SlowZVelocity:F2} mm/s).");

        // Fast descent to the approach point (still clear of the surface).
        var fast = await _engine.Motion.MoveAbsoluteAsync(g, "Z", approachZ, ct).ConfigureAwait(false);
        if (!fast.IsSuccess) return Result<MotionPosition>.Fail(fast.Error, $"Approach move: {fast.Message}");

        // Slow final descent to the touch height.
        var slow = await SlowZMoveAsync(g, downSign * approach, o.SlowZVelocity, ct).ConfigureAwait(false);
        if (!slow.IsSuccess) return Result<MotionPosition>.Fail(slow.Error, slow.Message);

        var pos = await _engine.Motion.GetPositionAsync(g).ConfigureAwait(false);
        if (!pos.IsSuccess) return Result<MotionPosition>.Fail(pos.Error, $"Read dispense position: {pos.Message}");
        return Result<MotionPosition>.Ok(pos.Value);
    }

    /// <summary>
    /// The automated touch height: <c>focusZ - FocusToNeedleZmm</c>, then shifted by the
    /// operator's <see cref="CameraNeedleOptions.TouchZAdjustMm"/> in the toward-surface
    /// direction (+ deeper, − higher). <paramref name="downSign"/> is the focus→surface
    /// direction, derived from the stored relationship so a tiny adjust can't flip it.
    /// </summary>
    private static double ComputeTouchZ(CameraNeedleOptions o, double focusZ, out double downSign)
    {
        downSign = Math.Sign(-o.FocusToNeedleZmm);
        if (downSign == 0) downSign = -1;
        return (focusZ - o.FocusToNeedleZmm) + downSign * o.TouchZAdjustMm;
    }

    /// <summary>Relative Z move at a slow velocity, restoring the prior axis velocity afterward.</summary>
    private async Task<Result> SlowZMoveAsync(string device, double dz, double slowVel, CancellationToken ct)
    {
        var prev = await _engine.Motion.GetVelocityAsync(device, "Z").ConfigureAwait(false);
        try
        {
            await _engine.Motion.SetVelocityAsync(device, "Z", slowVel).ConfigureAwait(false);
            return await _engine.Motion.MoveRelativeAsync(device, "Z", dz, ct).ConfigureAwait(false);
        }
        finally
        {
            if (prev.IsSuccess)
                await _engine.Motion.SetVelocityAsync(device, "Z", prev.Value).ConfigureAwait(false);
        }
    }

    /// <summary>Applies a relative XY move; skips an axis whose delta is ~0.</summary>
    private async Task<Result> ApplyRelativeXyAsync(string device, double dx, double dy, CancellationToken ct)
    {
        if (Math.Abs(dx) > 1e-6)
        {
            var rx = await _engine.Motion.MoveRelativeAsync(device, "X", dx, ct).ConfigureAwait(false);
            if (!rx.IsSuccess) return rx;
        }
        if (Math.Abs(dy) > 1e-6)
        {
            var ry = await _engine.Motion.MoveRelativeAsync(device, "Y", dy, ct).ConfigureAwait(false);
            if (!ry.IsSuccess) return ry;
        }
        return Result.Ok();
    }

    /// <summary>On abort, de-energize the shot output and raise the dispenser head so the
    /// needle is clear of the surface. Best-effort.</summary>
    private async Task FailSafeAsync(CameraNeedleOptions o)
    {
        try
        {
            await _engine.IO.SetOutputByNameAsync(o.ShotIoDevice, o.ShotPin, false, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, $"Camera-needle fail-safe shot-off failed: {ex.Message}");
        }
        try
        {
            await _engine.Pneumatic.RetractAndWaitAsync(o.DispenserSlide, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, $"Camera-needle fail-safe retract failed: {ex.Message}");
        }
    }

    private static Result<CameraNeedleResult> Fail(string message, ErrorCode code)
        => Result<CameraNeedleResult>.Fail(code, message);

    private static Result<CameraNeedleResult> Declined()
        => Result<CameraNeedleResult>.Fail(ErrorCode.OperationFailed, "Operator declined the calibration.");
}
