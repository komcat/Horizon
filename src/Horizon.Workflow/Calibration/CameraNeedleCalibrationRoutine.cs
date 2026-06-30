using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Dispensing;

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
    private readonly DispenseHead _head;

    public CameraNeedleCalibrationRoutine(
        IHorizonEngine engine, IOperatorPrompt? prompt, Action<LogLevel, string> log)
    {
        _engine = engine;
        _prompt = prompt;
        _log = log;
        _head = new DispenseHead(engine, log, "Camera-needle");
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
            await _head.FailSafeAsync(o.ShotIoDevice, o.ShotPin, o.DispenserSlide).ConfigureAwait(false);
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
        var ext = await _head.ExtendAsync(o.DispenserSlide, ct).ConfigureAwait(false);
        if (!ext.IsSuccess) return Result<MotionPosition>.Fail(ext.Error, ext.Message);

        // Reach the needle-touch height. First-time (no prior Z relationship): the operator
        // jogs Z down to find it. On re-runs the touch height is known relative to focus, so
        // we OFFER an auto-descent (fast to ApproachMm above touch, then crawl at SlowZVelocity);
        // the operator can decline and jog manually instead.
        Result<MotionPosition> reach;
        if (Math.Abs(o.FocusToNeedleZmm) > 1e-6)
        {
            double touchZ = DispenseHead.ComputeTouchZ(focusZ, o.FocusToNeedleZmm, o.TouchZAdjustMm, out _);
            bool auto = await _prompt!.ConfirmAsync("Auto needle touch?",
                    $"Auto-descend to the known touch height (Z≈{touchZ:F3} mm)? " +
                    "Confirm = auto-descend, Cancel = jog manually.", ct).ConfigureAwait(false);
            if (auto)
            {
                reach = await _head.AutoTouchAsync(
                    g, focusZ, o.FocusToNeedleZmm, o.ApproachMm, o.SlowZVelocity, o.TouchZAdjustMm, ct).ConfigureAwait(false);
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

        // Fire the shot (ON -> debounce -> dwell -> OFF -> debounce; OFF guaranteed).
        var shot = await _head.FireShotAsync(o.ShotIoDevice, o.ShotPin, o.OutputDebounceMs, o.DwellMs, ct).ConfigureAwait(false);
        if (!shot.IsSuccess) return Result<MotionPosition>.Fail(shot.Error, shot.Message);

        // Slow lift-off, away from the surface.
        var lift = await _head.LiftOffAsync(g, focusZ, gDispense.Z, o.LiftoffMm, o.SlowZVelocity, ct).ConfigureAwait(false);
        if (!lift.IsSuccess) return Result<MotionPosition>.Fail(lift.Error, lift.Message);

        // Raise the dispenser head (sensor-confirmed).
        var ret = await _head.RetractAsync(o.DispenserSlide, ct).ConfigureAwait(false);
        if (!ret.IsSuccess) return Result<MotionPosition>.Fail(ret.Error, ret.Message);

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

    private static Result<CameraNeedleResult> Fail(string message, ErrorCode code)
        => Result<CameraNeedleResult>.Fail(code, message);

    private static Result<CameraNeedleResult> Declined()
        => Result<CameraNeedleResult>.Fail(ErrorCode.OperationFailed, "Operator declined the calibration.");
}
