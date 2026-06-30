using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Dispensing;

/// <summary>
/// Shared low-level dispenser-head primitives used by both the camera→needle calibration
/// and the dispensing routines: lower / raise the head, auto-descend to a known touch
/// height, fire the epoxy shot output, and slow lift-off — plus the fail-safe that
/// de-energizes the shot and retracts on abort. Centralizing these keeps the bench-tuned
/// shot behavior in one place, so changing it changes every caller at once.
/// </summary>
internal sealed class DispenseHead
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;
    private readonly string _tag;

    /// <param name="tag">Log-line prefix, e.g. "Camera-needle" or "Dispense".</param>
    public DispenseHead(IHorizonEngine engine, Action<LogLevel, string> log, string tag)
    {
        _engine = engine;
        _log = log;
        _tag = tag;
    }

    /// <summary>Lowers the dispenser head (sensor-confirmed).</summary>
    public async Task<Result> ExtendAsync(string slide, CancellationToken ct)
    {
        _log(LogLevel.Info, $"{_tag}: extending dispenser head '{slide}'.");
        var ext = await _engine.Pneumatic.ExtendAndWaitAsync(slide, ct).ConfigureAwait(false);
        return ext.IsSuccess ? Result.Ok() : Result.Fail(ext.Error, $"Dispenser extend: {ext.Message}");
    }

    /// <summary>Raises the dispenser head (sensor-confirmed).</summary>
    public async Task<Result> RetractAsync(string slide, CancellationToken ct)
    {
        var ret = await _engine.Pneumatic.RetractAndWaitAsync(slide, ct).ConfigureAwait(false);
        return ret.IsSuccess ? Result.Ok() : Result.Fail(ret.Error, $"Dispenser retract: {ret.Message}");
    }

    /// <summary>
    /// The automated touch height: <c>focusZ - focusToNeedleZmm</c>, then shifted by
    /// <paramref name="touchZAdjustMm"/> in the toward-surface direction (+ deeper, − higher).
    /// <paramref name="downSign"/> is the focus→surface direction, derived from the stored
    /// relationship so a tiny adjust can't flip it.
    /// </summary>
    public static double ComputeTouchZ(double focusZ, double focusToNeedleZmm, double touchZAdjustMm, out double downSign)
    {
        downSign = Math.Sign(-focusToNeedleZmm);
        if (downSign == 0) downSign = -1;
        return (focusZ - focusToNeedleZmm) + downSign * touchZAdjustMm;
    }

    /// <summary>
    /// Fast-move to <paramref name="approachMm"/> above the known touch height, then crawl
    /// the rest at <paramref name="slowZVel"/>. Returns the gantry pose at touch.
    /// </summary>
    public async Task<Result<MotionPosition>> AutoTouchAsync(
        string gantry, double focusZ, double focusToNeedleZmm,
        double approachMm, double slowZVel, double touchZAdjustMm, CancellationToken ct)
    {
        double touchZ = ComputeTouchZ(focusZ, focusToNeedleZmm, touchZAdjustMm, out double downSign);
        double approach = Math.Abs(approachMm);
        double approachZ = touchZ - downSign * approach;   // ApproachMm short of touch, on the focus side

        _log(LogLevel.Info,
            $"{_tag}: auto-approach to touch Z={touchZ:F4} (fast to {approachZ:F4}, slow {approach:F1} mm @ {slowZVel:F2} mm/s).");

        var fast = await _engine.Motion.MoveAbsoluteAsync(gantry, "Z", approachZ, ct).ConfigureAwait(false);
        if (!fast.IsSuccess) return Result<MotionPosition>.Fail(fast.Error, $"Approach move: {fast.Message}");

        var slow = await SlowZMoveAsync(gantry, downSign * approach, slowZVel, ct).ConfigureAwait(false);
        if (!slow.IsSuccess) return Result<MotionPosition>.Fail(slow.Error, slow.Message);

        var pos = await _engine.Motion.GetPositionAsync(gantry).ConfigureAwait(false);
        if (!pos.IsSuccess) return Result<MotionPosition>.Fail(pos.Error, $"Read touch position: {pos.Message}");
        return Result<MotionPosition>.Ok(pos.Value);
    }

    /// <summary>
    /// Fires one epoxy shot: output ON → debounce → dwell (epoxy window) → OFF → debounce.
    /// A finally guarantees the output de-energizes even on cancel / error mid-dwell (using
    /// <see cref="CancellationToken.None"/> on the OFF so an aborting token can't skip it).
    /// </summary>
    public async Task<Result> FireShotAsync(
        string shotIoDevice, string shotPin, int debounceMs, int dwellMs, CancellationToken ct)
    {
        _log(LogLevel.Info, $"{_tag}: shot ON {shotIoDevice}/{shotPin} (debounce {debounceMs} ms, dwell {dwellMs} ms).");
        var on = await _engine.IO.SetOutputByNameAsync(shotIoDevice, shotPin, true, ct).ConfigureAwait(false);
        if (!on.IsSuccess) return Result.Fail(on.Error, $"Dispenser shot ON ({shotIoDevice}/{shotPin}): {on.Message}");
        try
        {
            if (debounceMs > 0) await Task.Delay(debounceMs, ct).ConfigureAwait(false);
            if (dwellMs > 0) await Task.Delay(dwellMs, ct).ConfigureAwait(false);
        }
        finally
        {
            var off = await _engine.IO.SetOutputByNameAsync(shotIoDevice, shotPin, false, CancellationToken.None).ConfigureAwait(false);
            _log(off.IsSuccess ? LogLevel.Info : LogLevel.Warning,
                $"{_tag}: shot OFF {shotIoDevice}/{shotPin}{(off.IsSuccess ? "." : $" FAILED: {off.Message}")}");
        }
        if (debounceMs > 0) await Task.Delay(debounceMs, ct).ConfigureAwait(false);
        return Result.Ok();
    }

    /// <summary>Slow lift-off from <paramref name="currentZ"/> toward focus (away from the surface).</summary>
    public Task<Result> LiftOffAsync(
        string gantry, double focusZ, double currentZ, double liftoffMm, double slowZVel, CancellationToken ct)
    {
        double upSign = Math.Sign(focusZ - currentZ);
        if (upSign == 0) upSign = 1;
        return SlowZMoveAsync(gantry, upSign * Math.Abs(liftoffMm), slowZVel, ct);
    }

    /// <summary>Relative Z move at a slow velocity, restoring the prior axis velocity afterward.</summary>
    public async Task<Result> SlowZMoveAsync(string device, double dz, double slowVel, CancellationToken ct)
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

    /// <summary>Best-effort de-energize of the shot output.</summary>
    public async Task ShotOffAsync(string shotIoDevice, string shotPin)
    {
        try
        {
            await _engine.IO.SetOutputByNameAsync(shotIoDevice, shotPin, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, $"{_tag} fail-safe shot-off failed: {ex.Message}");
        }
    }

    /// <summary>On abort, de-energize the shot output and raise the dispenser head. Best-effort.</summary>
    public async Task FailSafeAsync(string shotIoDevice, string shotPin, string slide)
    {
        await ShotOffAsync(shotIoDevice, shotPin).ConfigureAwait(false);
        try
        {
            await _engine.Pneumatic.RetractAndWaitAsync(slide, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, $"{_tag} fail-safe retract failed: {ex.Message}");
        }
    }
}
