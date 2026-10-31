using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Dispensing;

namespace Horizon.Workflow.Calibration;

/// <summary>
/// Camera→needle calibration — FireDog's <c>needle_calibration.lua</c> (itself bobcat's
/// "Calibrate Camera-to-Needle Offset") as a Layer 2 routine. Put a dot on a coupon, look at the
/// dot, subtract:
/// <code>
///   dx, dy = needle-touch pose − camera pose centred on the imprint   (add to a camera pose)
///   dz     = needle-touch Z − focused camera Z                         (add to a focused camera Z)
/// </code>
/// <para><b>Pass 1 measures</b>: the operator jogs the needle down onto the coupon.
/// <b>Pass 2+ verifies</b>: the machine drives the needle to where the calibration says it touches,
/// stops <see cref="CameraNeedleCalibrationData.VerifyGapMm"/> short, and the operator closes the
/// gap. How far they had to jog is the Z residual; where the dot lands is the XY residual. Verified
/// means XY AND Z inside tolerance — a dispense drives to <c>cam Z + dz</c> with nobody on the jog,
/// so dz is the number that most needs checking.</para>
///
/// <para>The motion of one pass (the needle-motion drawing): traverse needle over the spot at the
/// camera's Z → slide down → approach to the safety height → final to the verify gap (verify only)
/// → touch (operator) and shot → Z lifts <c>LiftMm</c> → slide up → camera back over the dot.
/// Every pass is saved through <c>savePass</c>; the offset is carried in locals between passes.
/// Operator interaction goes through <see cref="IOperatorPrompt.ConfirmAsync"/> (yes/no) and
/// <see cref="IOperatorPrompt.JogPopupAsync"/> (jog, then Continue).</para>
/// </summary>
internal sealed class CameraNeedleCalibrationRoutine
{
    private const string Tag = "Needle calibration";

    private readonly IHorizonEngine _engine;
    private readonly IOperatorPrompt? _prompt;
    private readonly Action<LogLevel, string> _log;
    private readonly Func<string, string, CancellationToken, Task<Result>> _moveToNamed;
    private readonly Action<CameraNeedleResult> _savePass;
    private readonly DispenseHead _head;

    public CameraNeedleCalibrationRoutine(
        IHorizonEngine engine, IOperatorPrompt? prompt, Action<LogLevel, string> log,
        Func<string, string, CancellationToken, Task<Result>> moveToNamed,
        Action<CameraNeedleResult> savePass)
    {
        _engine = engine;
        _prompt = prompt;
        _log = log;
        _moveToNamed = moveToNamed;
        _savePass = savePass;
        _head = new DispenseHead(engine, log, Tag);
    }

    /// <summary>The offset carried between passes (read once at the start, never re-read).</summary>
    private sealed class Arm
    {
        public double Dx, Dy, Dz;
        public bool Aim;    // dx/dy good enough to aim the needle at the spot (a seed counts)
        public bool AimZ;   // dz good enough to park above the previous touch
    }

    public async Task<Result<CameraNeedleResult>> RunAsync(CameraNeedleCalibrationData cfg, CancellationToken ct = default)
    {
        if (_prompt == null)
            return Result<CameraNeedleResult>.Fail(ErrorCode.NotSupported, "No operator-prompt handler registered.");
        if (string.IsNullOrWhiteSpace(cfg.GantryDevice))
            return Result<CameraNeedleResult>.Fail(ErrorCode.InvalidParameter, "No gantry set — name the device carrying the camera and needle.");

        var gantry = new NeedleGantry(_engine.Motion, cfg.GantryDevice, _log, Tag);
        var snap = await gantry.SnapshotAsync().ConfigureAwait(false);
        if (!snap.IsSuccess) return Result<CameraNeedleResult>.Fail(snap.Error, snap.Message);

        try
        {
            return await RunPassesAsync(cfg, gantry, ct).ConfigureAwait(false);
        }
        catch (NeedleProcedureException a)
        {
            _log(LogLevel.Warning, $"{Tag}: {a.Message}");
            return Result<CameraNeedleResult>.Fail(a.Code, a.Message);
        }
        catch (OperationCanceledException)
        {
            // STOP = no more gantry motion. Close the valve and bring the slide up.
            await _head.FailSafeAsync(cfg.ShotIoDevice, cfg.ShotPin, cfg.DispenserSlide).ConfigureAwait(false);
            return Result<CameraNeedleResult>.Fail(ErrorCode.ProcessCancelled, "Needle calibration stopped.");
        }
        finally
        {
            await gantry.RestoreAsync().ConfigureAwait(false);
        }
    }

    private async Task<Result<CameraNeedleResult>> RunPassesAsync(
        CameraNeedleCalibrationData cfg, NeedleGantry g, CancellationToken ct)
    {
        int passes = Math.Clamp(cfg.Passes, 1, 5);
        await ClearFirstAsync(cfg.ClearMoves, ct).ConfigureAwait(false);

        // The offset as it stands right now. A seed (not calibrated, non-zero dx/dy) is enough to
        // AIM with even though it is not enough to dispense through — which is what it is for.
        var arm = new Arm { Dx = cfg.VcamneedleX, Dy = cfg.VcamneedleY, Dz = cfg.Dz };
        arm.Aim = cfg.IsCalibrated || Math.Abs(arm.Dx) > 1e-9 || Math.Abs(arm.Dy) > 1e-9;
        arm.AimZ = cfg.IsCalibrated || Math.Abs(arm.Dz) > 1e-9;
        _log(LogLevel.Info, arm.Aim
            ? $"{Tag}: starting from ({arm.Dx:F4}, {arm.Dy:F4}, {arm.Dz:F4}) mm{(cfg.IsCalibrated ? "" : " (seed)")}."
            : $"{Tag}: nothing to aim with — you will jog the needle over the coupon by hand and hunt for the imprint.");

        CameraNeedleResult? last = null;
        for (int pass = 1; pass <= passes; pass++)
        {
            last = await RunPassAsync(pass, passes, cfg, arm, g, ct).ConfigureAwait(false);
            _savePass(last);   // every pass: a pass-2 abort must not throw away a good pass 1
            if (last.Verified)
            {
                _log(LogLevel.Info, $"{Tag}: pass {pass} agreed to within {last.ResidualMm:F4} mm XY, {last.ZResidualMm:+0.0000;-0.0000} mm Z — done.");
                break;
            }
        }

        var r = last!;
        if (passes == 1)
        {
            _log(LogLevel.Warning,
                $"{Tag}: offset ({arm.Dx:F4}, {arm.Dy:F4}, {arm.Dz:F4}) mm saved UNVERIFIED (passes = 1) — set passes ≥ 2 to re-measure and check it.");
            return Result<CameraNeedleResult>.Ok(r);
        }
        if (!r.Verified)
        {
            // Saved anyway (better than what was there) — but a measurement that will not repeat
            // points at the marking, the touch, or a needle deflecting on contact.
            return Result<CameraNeedleResult>.Fail(ErrorCode.OperationFailed,
                $"Needle calibration did not repeat: the last verify pass was off by {r.ResidualMm:F4} mm XY " +
                $"(tolerance {cfg.TolMm:F4}) and {r.ZResidualMm:+0.0000;-0.0000} mm Z (tolerance {cfg.ZTolMm:F4}). The offset " +
                $"({arm.Dx:F4}, {arm.Dy:F4}, {arm.Dz:F4}) mm was saved anyway — check the imprint quality, the touch, " +
                "and that the needle is not deflecting on contact before dispensing through it.");
        }
        _log(LogLevel.Info,
            $"{Tag}: calibrated ({arm.Dx:F4}, {arm.Dy:F4}, {arm.Dz:F4}) mm on {cfg.GantryDevice}, verified to {r.ResidualMm:F4} mm XY, {r.ZResidualMm:+0.0000;-0.0000} mm Z.");
        return Result<CameraNeedleResult>.Ok(r);
    }

    /// <summary>Out of the camera's way first (the hexapods), before the coupon prompt.</summary>
    private async Task ClearFirstAsync(IReadOnlyList<NeedleClearMove> moves, CancellationToken ct)
    {
        foreach (var m in moves)
        {
            if (string.IsNullOrWhiteSpace(m.Device) || string.IsNullOrWhiteSpace(m.Position)) continue;
            _log(LogLevel.Info, $"{Tag}: clearing {m.Device} → '{m.Position}' first.");
            Must(await _moveToNamed(m.Device, m.Position, ct).ConfigureAwait(false), $"Clear first: {m.Device} → '{m.Position}'");
        }
    }

    private async Task<CameraNeedleResult> RunPassAsync(
        int pass, int passes, CameraNeedleCalibrationData cfg, Arm arm, NeedleGantry g, CancellationToken ct)
    {
        bool verify = pass > 1;

        // ---- 1. A clean spot on the coupon ----
        await AskAsync(pass == 1 ? "Coupon" : $"Pass {pass} of {passes} — verify",
            pass == 1
                ? "Put a flat, matte coupon under the camera — something an epoxy dot will show up on. Confirm when it is in place."
                : "Move the coupon to a CLEAN spot (or slide a fresh one in) — a second imprint on the first has nothing to measure. Then Confirm.",
            ct).ConfigureAwait(false);
        await JogAsync(g, "Camera on the coupon",
            "Centre the camera on a clean part of the coupon and FOCUS it with Z — dZ is measured down from this focal plane. Then Continue.",
            "XYZ", 0.1, ct).ConfigureAwait(false);

        // ---- 2. The camera reference ----
        var cam = await ReadAsync(g).ConfigureAwait(false);
        _log(LogLevel.Info, $"{Tag}: pass {pass}: camera at ({cam.X:F4}, {cam.Y:F4}, {cam.Z:F4}).");

        // ---- 3. Traverse: the needle over that spot, at the CAMERA's Z (well clear, slide up) ----
        if (arm.Aim)
            Must(await g.MoveXyzAsync(cam.X + arm.Dx, cam.Y + arm.Dy, cam.Z, cfg.VTraverse, ct).ConfigureAwait(false),
                "Traverse the needle over the spot");

        await AskAsync("Extend dispenser",
            $"About to extend '{cfg.DispenserSlide}'. Check the needle has room to come down, then Confirm.", ct).ConfigureAwait(false);

        var tip = await NeedleDownAsync(pass, verify, cfg, arm, cam, g, ct).ConfigureAwait(false);

        // ---- 7. Traverse: the camera back over the dot ----
        // With an offset to aim by, the dot is where the camera stood when the pass started.
        // With nothing to aim by, climb back to the camera's Z and hunt from here.
        if (arm.Aim)
        {
            Must(await g.MoveXyzAsync(cam.X, cam.Y, cam.Z, cfg.VTraverse, ct).ConfigureAwait(false), "Camera back over the dot");
        }
        else
        {
            Must(await g.MoveZAsync(cam.Z, cfg.VTraverse, ct).ConfigureAwait(false), "Climb back to the camera's Z");
            await JogAsync(g, "Find the imprint",
                "The imprint is roughly one needle-to-lens distance from here, in the direction the lens sits from the needle. " +
                "Jog until the dot is in the picture, then Continue.", "XY", 0.1, ct).ConfigureAwait(false);
        }

        await JogAsync(g, "Centre on the imprint",
            (arm.Aim ? "The imprint should be near the crosshair — off by the current calibration's error. " : "") +
            "Jog / MoveClick until the crosshair sits on the CENTRE of the imprint, then Continue.",
            "XY", 0.01, ct).ConfigureAwait(false);
        var dot = await ReadAsync(g).ConfigureAwait(false);

        // ---- 8. Subtract ----
        double ndx = tip.X - dot.X, ndy = tip.Y - dot.Y, ndz = tip.Z - cam.Z;
        double residual = 0, zres = 0;
        if (verify)
        {
            residual = Math.Sqrt((ndx - arm.Dx) * (ndx - arm.Dx) + (ndy - arm.Dy) * (ndy - arm.Dy));
            zres = (ndz - arm.Dz) * (arm.Dz >= 0 ? 1.0 : -1.0);   // + = touched lower than predicted
            _log(LogLevel.Info,
                $"{Tag}: pass {pass}: ({ndx:F4}, {ndy:F4}, {ndz:F4}) mm — XY residual {residual:F4} mm, Z residual {zres:+0.0000;-0.0000} mm.");
        }
        else
        {
            _log(LogLevel.Info, arm.Aim
                ? $"{Tag}: pass {pass}: ({ndx:F4}, {ndy:F4}, {ndz:F4}) mm — moved the XY answer by {Math.Sqrt((ndx - arm.Dx) * (ndx - arm.Dx) + (ndy - arm.Dy) * (ndy - arm.Dy)):F4} mm."
                : $"{Tag}: pass {pass}: ({ndx:F4}, {ndy:F4}, {ndz:F4}) mm — first measurement.");
        }

        arm.Dx = ndx; arm.Dy = ndy; arm.Dz = ndz;
        arm.Aim = arm.AimZ = true;
        bool verified = verify && residual <= cfg.TolMm && Math.Abs(zres) <= cfg.ZTolMm;

        return new CameraNeedleResult(ndx, ndy, -ndz,
            new CalPoint(cam.X, cam.Y, cam.Z), tip, new CalPoint(dot.X, dot.Y, dot.Z),
            pass, verify, residual, zres, verified);
    }

    /// <summary>
    /// Stages 2–6, with the needle DOWN: slide down, touch (operator), shot, lift, slide up. Any
    /// failure but a STOP lifts the needle off and retracts the slide before the error goes on,
    /// instead of leaving it standing on the coupon.
    /// </summary>
    private async Task<CalPoint> NeedleDownAsync(
        int pass, bool verify, CameraNeedleCalibrationData cfg, Arm arm, MotionPosition cam, NeedleGantry g, CancellationToken ct)
    {
        try
        {
            Must(await _head.ExtendAsync(cfg.DispenserSlide, ct).ConfigureAwait(false), "Extend the dispenser");
            if (cfg.SettleMs > 0) await Task.Delay(cfg.SettleMs, ct).ConfigureAwait(false);

            // Nothing aimed the needle: it hangs a lever arm away from the spot. Coarse XY by eye.
            if (!arm.Aim)
                await JogAsync(g, "Needle over the coupon",
                    "Get the needle over a clean part of the coupon — jog XY, or just slide the coupon under it. " +
                    "Watch the needle, not the screen. Continue when it is over it.", "XY", 0.1, ct).ConfigureAwait(false);

            double dir = arm.Dz >= 0 ? 1.0 : -1.0;
            double predicted = cam.Z + arm.Dz;   // where the calibration says it touches
            if (verify)
            {
                double target = predicted - dir * cfg.VerifyGapMm;
                double safety = target - dir * cfg.ClearMm;
                Must(await g.MoveZAsync(safety, cfg.VApproach, ct).ConfigureAwait(false), "Approach to the safety height");
                Must(await g.MoveZAsync(target, cfg.VFinal, ct).ConfigureAwait(false), "Final approach to the verify gap");
                _log(LogLevel.Info, $"{Tag}: pass {pass}: stopped {cfg.VerifyGapMm:F3} mm short of the predicted touch Z {predicted:F4}.");
                await JogAsync(g, "VERIFY — close the gap",
                    $"The needle stopped {cfg.VerifyGapMm:F3} mm above where the calibration says it touches. Jog Z DOWN until it " +
                    "just touches — barely, not pressing. If it is ALREADY pressing, jog UP until it just touches. Then Continue.",
                    "Z", 0.01, ct).ConfigureAwait(false);
            }
            else
            {
                // The previous touch height may belong to a needle that has since been CHANGED —
                // park a wider first-clear above it and hand the whole last stretch to the operator.
                if (arm.AimZ)
                {
                    Must(await g.MoveZAsync(predicted - dir * cfg.FirstClearMm, cfg.VApproach, ct).ConfigureAwait(false),
                        "Park above the previous touch height");
                    _log(LogLevel.Info, $"{Tag}: pass {pass}: parked {cfg.FirstClearMm:F3} mm short of the previous touch height.");
                }
                await JogAsync(g, "Touch the coupon",
                    "Jog Z DOWN until the needle just touches the coupon — barely, not pressing. Coarse steps first, " +
                    "10 µm for the last few tenths. Then Continue.", "Z", 0.1, ct).ConfigureAwait(false);
            }
            var tipPos = await ReadAsync(g).ConfigureAwait(false);
            var tip = new CalPoint(tipPos.X, tipPos.Y, tipPos.Z);

            // ---- 5. The imprint ----
            await AskAsync("Fire the shot", $"Fire a {cfg.ShotMs} ms shot to make the imprint?", ct).ConfigureAwait(false);
            Must(await _head.PulseShotAsync(cfg.ShotIoDevice, cfg.ShotPin, cfg.ShotMs, ct).ConfigureAwait(false), "Shot");

            // ---- 6. Lift off, THEN the slide — the needle comes straight up off the dot ----
            Must(await g.MoveZAsync(NeedleGantry.LiftToward(tip.Z, cam.Z, cfg.LiftMm), cfg.VLiftoff, ct).ConfigureAwait(false), "Lift off");
            Must(await _head.RetractAsync(cfg.DispenserSlide, ct).ConfigureAwait(false), "Retract the dispenser");
            return tip;
        }
        catch (NeedleProcedureException)
        {
            await LiftOffAfterFailureAsync(cfg, cam.Z, g).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Best-effort: valve closed, Z lifted toward the camera's Z, slide up.</summary>
    private async Task LiftOffAfterFailureAsync(CameraNeedleCalibrationData cfg, double camZ, NeedleGantry g)
    {
        await _head.ShotOffAsync(cfg.ShotIoDevice, cfg.ShotPin).ConfigureAwait(false);
        var at = await g.ReadAsync().ConfigureAwait(false);
        if (at.IsSuccess)
        {
            var lift = await g.MoveZAsync(NeedleGantry.LiftToward(at.Value.Z, camZ, cfg.LiftMm), cfg.VLiftoff, CancellationToken.None)
                .ConfigureAwait(false);
            if (!lift.IsSuccess) _log(LogLevel.Warning, $"{Tag}: lift-off after the failure failed: {lift.Message}");
        }
        await _head.RetractAsync(cfg.DispenserSlide, CancellationToken.None).ConfigureAwait(false);
    }

    // --- operator + motion helpers (failures become an Abort) ---

    private async Task AskAsync(string title, string message, CancellationToken ct)
    {
        if (!await _prompt!.ConfirmAsync(title, message, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            throw new NeedleProcedureException("Cancelled by the operator.", ErrorCode.ProcessCancelled);
        }
    }

    private async Task JogAsync(NeedleGantry g, string title, string message, string axes, double stepMm, CancellationToken ct)
    {
        var request = new JogPopupRequest(title, message, new[] { g.Device }, axes, stepMm, 3600);
        if (!await _prompt!.JogPopupAsync(request, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            throw new NeedleProcedureException("Cancelled by the operator.", ErrorCode.ProcessCancelled);
        }
    }

    private static async Task<MotionPosition> ReadAsync(NeedleGantry g)
    {
        var r = await g.ReadAsync().ConfigureAwait(false);
        if (!r.IsSuccess) throw new NeedleProcedureException(r.Message, r.Error);
        return r.Value;
    }

    private static void Must(Result r, string what)
    {
        if (!r.IsSuccess) throw new NeedleProcedureException($"{what}: {r.Message}", r.Error);
    }
}
