using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.Calibration;

namespace Horizon.Workflow.Dispensing;

/// <summary>
/// Runs a saved <see cref="DispenseRoutine"/> where the camera is looking — FireDog's
/// <c>dispense_procedure.lua</c>, extended to Horizon's Dot / Grid / Line patterns. The program
/// has put the camera over the target; the machine camera→needle calibration turns "where the
/// camera stands" into "where the needle stands":
/// <code>
///   touch  = cam Z + dz + dir·trim      needle ON the part (trim corrects the calibration)
///   stop   = touch − dir·gap            where it actually stops (gap is a deliberate standoff)
///   safety = stop − dir·safety          fast → slow handover
/// </code>
/// <para>The motion (the needle-motion drawing): [adjust X/Y] → needle over the first dot at the
/// camera's Z → [confirm] slide down → v_approach to safety → v_final to stop → [confirm] before /
/// shot / after → Z lifts <c>LiftMm</c> → (next dot: hop over at max(lift, safety), descend again)
/// → slide up → Z climbs to the camera's Z → [camera back over the pattern, check it]. XY never
/// moves while the needle is down near a dot. The gantry's velocity is read before the first move
/// and put back on every exit.</para>
/// </summary>
internal sealed class DispenseRoutineRunner
{
    private readonly IHorizonEngine _engine;
    private readonly IOperatorPrompt? _prompt;
    private readonly Action<LogLevel, string> _log;
    private readonly DispenseHead _head;

    public DispenseRoutineRunner(IHorizonEngine engine, IOperatorPrompt? prompt, Action<LogLevel, string> log)
    {
        _engine = engine;
        _prompt = prompt;
        _log = log;
        _head = new DispenseHead(engine, log, "Dispense");
    }

    /// <summary>The heights of one run, all in raw axis Z of the gantry.</summary>
    private readonly record struct Heights(double CamZ, double Touch, double Stop, double Safety, double Hop);

    /// <summary>What a failure path needs to take the needle off the part.</summary>
    private sealed class RunState
    {
        public bool NeedleDown;
        public double CamZ = double.NaN;
    }

    public async Task<Result> RunAsync(DispenseRoutine routine, CameraNeedleCalibrationData cal, CancellationToken ct = default)
    {
        var shot = routine.Shot;
        string tag = $"Dispense '{routine.Name}'";

        // The lever arm first, and hard: everything below is a move computed from it.
        if (!cal.IsCalibrated)
            return Result.Fail(ErrorCode.OperationFailed,
                "Needle not calibrated — nothing knows where the needle is relative to the camera. Run Calibration › Camera → Needle first.");
        if (string.IsNullOrWhiteSpace(cal.GantryDevice))
            return Result.Fail(ErrorCode.InvalidParameter, "The needle calibration names no gantry.");
        bool needsPrompt = shot.Adjust || shot.Confirm || (shot.VerifyBack && shot.VerifyMs > 0);
        if (needsPrompt && _prompt == null)
            return Result.Fail(ErrorCode.NotSupported,
                $"{tag}: adjust / confirm / verify need an operator prompt and none is registered.");
        if (!cal.Verified)
            _log(LogLevel.Warning, $"{tag}: dispensing through an UNVERIFIED needle calibration — measured once, never re-checked.");

        var g = new NeedleGantry(_engine.Motion, cal.GantryDevice, _log, tag);
        var snap = await g.SnapshotAsync().ConfigureAwait(false);
        if (!snap.IsSuccess) return snap;

        var state = new RunState();
        try
        {
            // ---- 1. Camera over the point (+ the operator's X/Y nudge, this run only) ----
            var taught = await ReadAsync(g).ConfigureAwait(false);
            if (shot.Adjust)
                await JogAsync(g, "Centre on the dispense point",
                    "Centre the camera crosshair on the dispense point (X/Y only), then Continue. The dot goes where you leave it.",
                    ct).ConfigureAwait(false);
            var cam = await ReadAsync(g).ConfigureAwait(false);
            double camZ = state.CamZ = taught.Z;   // Z is the focus the touch height is computed from — a hand-jogged Z is ignored
            if (shot.Adjust)
                _log(LogLevel.Info, $"{tag}: operator adjusted X {cam.X - taught.X:+0.0000;-0.0000}, Y {cam.Y - taught.Y:+0.0000;-0.0000} mm from the taught pose.");

            var h = ComputeHeights(cal, shot, camZ);
            var points = routine.Pattern.Expand();
            double baseX = cam.X + cal.VcamneedleX, baseY = cam.Y + cal.VcamneedleY;
            _log(LogLevel.Info,
                $"{tag}: {points.Count} dot(s), camera ({cam.X:F4}, {cam.Y:F4}, {camZ:F4}), touch Z {h.Touch:F4}, " +
                $"gap {shot.GapMm:F3} → stop Z {h.Stop:F4} (safety {h.Safety:F4}).");

            // ---- 2. Needle over the first dot, still at the camera's Z (slide up, well clear) ----
            Must(await g.MoveXyzAsync(baseX + points[0].Dx, baseY + points[0].Dy, camZ, shot.VTravel, ct).ConfigureAwait(false),
                "Needle over the point");
            if (shot.Confirm)
                await AskAsync("Extend dispenser",
                    $"Needle is over the point. Extend '{cal.DispenserSlide}' and dispense {points.Count} dot(s)?", ct).ConfigureAwait(false);
            state.NeedleDown = true;
            Must(await _head.ExtendAsync(cal.DispenserSlide, ct).ConfigureAwait(false), "Extend the dispenser");

            // ---- 3–7. Down, shot, lift — per dot ----
            Must(await g.MoveZAsync(h.Safety, shot.VApproach, ct).ConfigureAwait(false), "Approach to the safety height");
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0)
                {
                    Must(await g.MoveXyAsync(baseX + points[i].Dx, baseY + points[i].Dy, shot.VTravel, ct).ConfigureAwait(false),
                        $"Traverse to dot {i + 1}");
                    if (Math.Abs(h.Hop - h.Safety) > 1e-6)
                        Must(await g.MoveZAsync(h.Safety, shot.VApproach, ct).ConfigureAwait(false), $"Approach (dot {i + 1})");
                }
                await DotAsync(i, points.Count, cal, shot, h, g, ct).ConfigureAwait(false);
                if (i < points.Count - 1)
                    Must(await g.MoveZAsync(h.Hop, shot.VLiftoff, ct).ConfigureAwait(false), $"Hop height (dot {i + 1})");
            }

            // ---- Retreat: (lifted) → slide up → Z all the way to the camera's Z, THEN anything sideways ----
            Must(await _head.RetractAsync(cal.DispenserSlide, ct).ConfigureAwait(false), "Retract the dispenser");
            state.NeedleDown = false;
            Must(await g.MoveZAsync(camZ, shot.VLiftoff, ct).ConfigureAwait(false), "Climb to the camera's Z");

            // ---- 8. Look at the result ----
            if (shot.VerifyBack)
                await VerifyAsync(cam.X, cam.Y, camZ, shot, g, ct).ConfigureAwait(false);

            _log(LogLevel.Info, $"{tag}: dispensed {points.Count} × {shot.ShotMs} ms, needle stop Z {h.Stop:F4} ({shot.GapMm:F3} mm above touch).");
            return Result.Ok();
        }
        catch (NeedleProcedureException a)
        {
            if (state.NeedleDown) await RetreatAfterFailureAsync(cal, shot, state.CamZ, g, tag).ConfigureAwait(false);
            _log(LogLevel.Warning, $"{tag}: {a.Message}");
            return Result.Fail(a.Code, $"{tag}: {a.Message}");
        }
        catch (OperationCanceledException)
        {
            // STOP = no more gantry motion. Close the valve and bring the slide up.
            await _head.FailSafeAsync(cal.ShotIoDevice, cal.ShotPin, cal.DispenserSlide).ConfigureAwait(false);
            return Result.Fail(ErrorCode.ProcessCancelled, $"{tag} stopped.");
        }
        finally
        {
            await g.RestoreAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The run's heights. Trim and gap are applied along the measured "down" so neither assumes
    /// which way Z counts. Between dots the needle hops at max(lift, safety) above the stop height.
    /// </summary>
    private static Heights ComputeHeights(CameraNeedleCalibrationData cal, DispenseShot shot, double camZ)
    {
        double dir = cal.DownSign;
        double touch = camZ + cal.Dz + dir * shot.ZTrimMm;
        double stop = touch - dir * Math.Max(0, shot.GapMm);
        double safety = stop - dir * Math.Max(0, shot.SafetyMm);
        double hop = stop - dir * Math.Max(Math.Max(0, shot.LiftMm), Math.Max(0, shot.SafetyMm));
        return new Heights(camZ, touch, stop, safety, hop);
    }

    /// <summary>One dot: v_final to the stop height, [confirm the first], before / shot / after, lift off.</summary>
    private async Task DotAsync(
        int i, int count, CameraNeedleCalibrationData cal, DispenseShot shot, Heights h, NeedleGantry g, CancellationToken ct)
    {
        Must(await g.MoveZAsync(h.Stop, shot.VFinal, ct).ConfigureAwait(false), $"Final approach (dot {i + 1})");
        if (i == 0 && shot.Confirm)
            await AskAsync("Fire", count == 1
                ? $"At dispense height. Fire a {shot.ShotMs} ms shot?"
                : $"At dispense height. Fire {count} shots of {shot.ShotMs} ms?", ct).ConfigureAwait(false);

        if (shot.BeforeMs > 0) await Task.Delay(shot.BeforeMs, ct).ConfigureAwait(false);
        Must(await _head.PulseShotAsync(cal.ShotIoDevice, cal.ShotPin, shot.ShotMs, ct).ConfigureAwait(false), $"Shot (dot {i + 1})");
        if (shot.AfterMs > 0) await Task.Delay(shot.AfterMs, ct).ConfigureAwait(false);

        // A controlled straight pull off the dot instead of the cylinder snapping the needle away.
        if (shot.LiftMm > 0)
            Must(await g.MoveZAsync(NeedleGantry.LiftToward(h.Stop, h.CamZ, shot.LiftMm), shot.VLiftoff, ct).ConfigureAwait(false),
                $"Lift off (dot {i + 1})");
    }

    /// <summary>Camera back over the pattern, then let the operator check it.</summary>
    private async Task VerifyAsync(double camX, double camY, double camZ, DispenseShot shot, NeedleGantry g, CancellationToken ct)
    {
        Must(await g.MoveXyzAsync(camX, camY, camZ, shot.VTravel, ct).ConfigureAwait(false), "Camera back over the dot");
        bool ok = true;
        if (shot.Confirm)
            ok = await _prompt!.ConfirmAsync("Check the dispense", "Does the dispense look right?", ct).ConfigureAwait(false);
        else if (shot.VerifyMs > 0)
            ok = await _prompt!.CountdownAsync("Check the dispense", "Check the dot. Cancel if it is wrong.", shot.VerifyMs, ct)
                .ConfigureAwait(false);
        if (!ok)
        {
            ct.ThrowIfCancellationRequested();
            throw new NeedleProcedureException("Dispense rejected by the operator.", ErrorCode.ProcessCancelled);
        }
    }

    /// <summary>
    /// Best-effort retreat with the needle down, in the drawing's order: valve closed, Z lifts off
    /// toward the camera's Z, slide up, Z to the camera's Z. Leaving the needle on the part because
    /// the operator said no (or a leg failed) would be worse than the dispense.
    /// </summary>
    private async Task RetreatAfterFailureAsync(CameraNeedleCalibrationData cal, DispenseShot shot, double camZ, NeedleGantry g, string tag)
    {
        await _head.ShotOffAsync(cal.ShotIoDevice, cal.ShotPin).ConfigureAwait(false);
        var at = await g.ReadAsync().ConfigureAwait(false);
        if (at.IsSuccess && shot.LiftMm > 0)
            Warn(await g.MoveZAsync(NeedleGantry.LiftToward(at.Value.Z, camZ, shot.LiftMm), shot.VLiftoff, CancellationToken.None)
                .ConfigureAwait(false), tag, "lift-off");
        Warn(await _head.RetractAsync(cal.DispenserSlide, CancellationToken.None).ConfigureAwait(false), tag, "retract");
        Warn(await g.MoveZAsync(camZ, shot.VLiftoff, CancellationToken.None).ConfigureAwait(false), tag, "climb to the camera's Z");
    }

    private void Warn(Result r, string tag, string what)
    {
        if (!r.IsSuccess) _log(LogLevel.Warning, $"{tag}: retreat {what} failed: {r.Message}");
    }

    private async Task AskAsync(string title, string message, CancellationToken ct)
    {
        if (!await _prompt!.ConfirmAsync(title, message, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            throw new NeedleProcedureException("Cancelled by the operator.", ErrorCode.ProcessCancelled);
        }
    }

    private async Task JogAsync(NeedleGantry g, string title, string message, CancellationToken ct)
    {
        var request = new JogPopupRequest(title, message, new[] { g.Device }, "XY", 0.01, 3600);
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
