using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// <b>DEPRECATED — do not use or build on this.</b> Kept only so existing programs with a
/// <c>motion.slowApproach</c> step still run; a slow final approach is
/// <see cref="MoveApproach"/> (Move to Position's approach distance / velocity).
/// The guarded slow move behind the <c>motion.slowApproach</c> step: snapshot the device's
/// current velocity, drop it to a crawl, move to the target ONE AXIS AT A TIME in the fixed
/// order Z → Y → X while a STOP watch is shown, then put the snapshot velocity back — always,
/// also on STOP, a move fault or a run cancel. Refuses (no velocity change, no motion) when
/// the snapshot can't be read (it could not be restored), or when the target is too far for
/// a crawl.
/// </summary>
internal static class SlowApproach
{
    /// <summary>The facet's move wait times out at 60 s — refuse an axis crawl that would need longer.</summary>
    public const double MaxMoveSeconds = 55.0;

    /// <summary>Axis move order — Z first, then Y, then X.</summary>
    private static readonly string[] AxisOrder = { "Z", "Y", "X" };

    /// <summary>An axis already this close to its target (mm) is skipped.</summary>
    private const double AtTargetMm = 0.0001;

    /// <summary>
    /// Runs the slow move. Returns <c>Ok(true)</c> when the operator pressed STOP (the device
    /// was halted short of the target and the remaining axes were skipped), <c>Ok(false)</c>
    /// when the target was reached, or a failure (refused / move fault / velocity restore
    /// fault). <paramref name="watchStop"/> shows the STOP window: it completes <c>true</c>
    /// when STOP is pressed and is closed (its token cancelled) as soon as the moves end.
    /// </summary>
    public static async Task<Result<bool>> RunAsync(
        IMotionFacet motion, string device, MotionPosition target, string axes, double speed,
        double maxDistanceMm, Func<CancellationToken, Task<bool>> watchStop,
        Action<LogLevel, string> log, CancellationToken ct)
    {
        var info = motion.GetDevice(device);
        if (info == null)
            return Result<bool>.Fail(ErrorCode.MotionDeviceNotFound, $"motion device '{device}' not found.");

        bool isPi = info.ControllerType.Equals("PI", StringComparison.OrdinalIgnoreCase);
        // Installed axes the step asked for, in the fixed Z → Y → X move order.
        var axisNames = AxisOrder
            .Where(a => axes.Contains(a, StringComparison.OrdinalIgnoreCase))
            .Select(a => info.Axes.FirstOrDefault(i => i.Equals(a, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .ToList();
        if (axisNames.Count == 0)
            return Result<bool>.Fail(ErrorCode.InvalidParameter, $"no axes of '{axes}' are installed on {device}.");

        speed = MotionVelocityLimits.Clamp(speed, info.ControllerType);
        if (speed <= 0)
            return Result<bool>.Fail(ErrorCode.InvalidParameter, "approach speed must be > 0 mm/s.");

        // Distance guard — a crawl is for the last few mm, never a long traverse. The axes
        // move one after another, so the path is the sum of the per-axis travels.
        var cur = await motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!cur.IsSuccess) return Result<bool>.Fail(cur.Error, $"read position: {cur.Message}");
        double path = 0;
        foreach (var axis in axisNames)
        {
            double d = Math.Abs(Coord(target, axis) - Coord(cur.Value, axis));
            if (d / speed > MaxMoveSeconds)
                return Result<bool>.Fail(ErrorCode.InvalidParameter,
                    $"{axis} {d:F3} mm at {speed:F2} mm/s needs {d / speed:F0} s — over the {MaxMoveSeconds:F0} s move limit.");
            path += d;
        }
        if (path > maxDistanceMm)
            return Result<bool>.Fail(ErrorCode.InvalidParameter,
                $"target is {path:F3} mm of travel away — more than the {maxDistanceMm:F1} mm approach limit.");

        // Snapshot BEFORE touching anything. A read of 0 means the controller didn't answer
        // (the adapter returns 0 on failure) — refuse rather than "restore" a zero velocity.
        var snapshot = await ReadVelocitiesAsync(motion, device, isPi, axisNames).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<bool>.Fail(snapshot.Error, snapshot.Message);

        log(LogLevel.Info, $"Slow approach {device}: velocity snapshot " +
            string.Join(", ", snapshot.Value.Select(kv => $"{kv.Key}={kv.Value:F3}")) +
            $" → {speed:F3} mm/s, axes {string.Join("→", axisNames)}, {path:F3} mm to go.");

        Result<bool> outcome;
        Result restored;
        try
        {
            outcome = await CrawlAsync(motion, device, cur.Value, target, speed, isPi, axisNames, watchStop, log, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // Always restore — STOP, a fault and a run cancel all land here. Not tied to ct.
            restored = await WriteVelocitiesAsync(motion, device, snapshot.Value).ConfigureAwait(false);
            log(restored.IsSuccess ? LogLevel.Info : LogLevel.Error, restored.IsSuccess
                ? $"Slow approach {device}: velocity restored."
                : $"Slow approach {device}: velocity restore FAILED — {restored.Message}");
        }

        if (!restored.IsSuccess && outcome.IsSuccess)
            return Result<bool>.Fail(restored.Error,
                $"approach done, but restoring the velocity failed — {restored.Message}");
        return outcome;
    }

    /// <summary>
    /// Sets the crawl speed, then moves each axis in turn (Z → Y → X), racing every move
    /// against the one STOP window. STOP halts the device and skips the remaining axes;
    /// a move fault stops the sequence there.
    /// </summary>
    private static async Task<Result<bool>> CrawlAsync(
        IMotionFacet motion, string device, MotionPosition start, MotionPosition target, double speed,
        bool isPi, IReadOnlyList<string> axisNames, Func<CancellationToken, Task<bool>> watchStop,
        Action<LogLevel, string> log, CancellationToken ct)
    {
        var slow = isPi
            ? new Dictionary<string, double> { [SystemKey] = speed }
            : axisNames.ToDictionary(a => a, _ => speed);
        var set = await WriteVelocitiesAsync(motion, device, slow).ConfigureAwait(false);
        if (!set.IsSuccess) return Result<bool>.Fail(set.Error, $"set approach speed: {set.Message}");

        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stopTask = watchStop(watchCts.Token);
        try
        {
            foreach (var axis in axisNames)
            {
                double to = Coord(target, axis);
                if (Math.Abs(to - Coord(start, axis)) < AtTargetMm) continue;

                var moveTask = motion.MoveAbsoluteAsync(device, axis, to, ct);
                var first = await Task.WhenAny(moveTask, stopTask).ConfigureAwait(false);
                if (first == stopTask && stopTask.IsCompletedSuccessfully && stopTask.Result)
                {
                    var halt = await motion.StopAsync(device).ConfigureAwait(false);
                    log(halt.IsSuccess ? LogLevel.Warning : LogLevel.Error, halt.IsSuccess
                        ? $"Slow approach {device}: STOPPED by operator during {axis}."
                        : $"Slow approach {device}: operator STOP during {axis} — stop command failed: {halt.Message}");
                    await moveTask.ConfigureAwait(false);   // returns once the axis has halted
                    return Result<bool>.Ok(true);
                }

                var moved = await moveTask.ConfigureAwait(false);
                if (!moved.IsSuccess)
                    return Result<bool>.Fail(moved.Error, $"{axis}: {moved.Message}");
                log(LogLevel.Info, $"Slow approach {device}: {axis} → {to:F4}.");
            }
            return Result<bool>.Ok(false);
        }
        finally
        {
            // Moves ended (or were stopped) — close the STOP window, then collect its task.
            watchCts.Cancel();
            try { await stopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* window closed by the moves ending — expected */ }
        }
    }

    /// <summary>Key used in a velocity map for a PI device's single system (path) velocity.</summary>
    private const string SystemKey = "system";

    private static async Task<Result<Dictionary<string, double>>> ReadVelocitiesAsync(
        IMotionFacet motion, string device, bool isPi, IReadOnlyList<string> axisNames)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (isPi)
        {
            var r = await motion.GetSystemVelocityAsync(device).ConfigureAwait(false);
            if (!r.IsSuccess || r.Value <= 0)
                return Result<Dictionary<string, double>>.Fail(ErrorCode.MotionCommunicationError,
                    $"could not read {device} system velocity — approach refused (it could not be restored).");
            map[SystemKey] = r.Value;
            return Result<Dictionary<string, double>>.Ok(map);
        }

        foreach (var axis in axisNames)
        {
            var r = await motion.GetVelocityAsync(device, axis).ConfigureAwait(false);
            if (!r.IsSuccess || r.Value <= 0)
                return Result<Dictionary<string, double>>.Fail(ErrorCode.MotionCommunicationError,
                    $"could not read {device}.{axis} velocity — approach refused (it could not be restored).");
            map[axis] = r.Value;
        }
        return Result<Dictionary<string, double>>.Ok(map);
    }

    /// <summary>Writes every entry of a velocity map; tries them all, reports the first failure.</summary>
    private static async Task<Result> WriteVelocitiesAsync(
        IMotionFacet motion, string device, IReadOnlyDictionary<string, double> velocities)
    {
        Result firstFailure = Result.Ok();
        foreach (var (axis, v) in velocities)
        {
            var r = axis == SystemKey
                ? await motion.SetSystemVelocityAsync(device, v).ConfigureAwait(false)
                : await motion.SetVelocityAsync(device, axis, v).ConfigureAwait(false);
            if (!r.IsSuccess && firstFailure.IsSuccess) firstFailure = r;
        }
        return firstFailure;
    }

    /// <summary>The X / Y / Z coordinate of a position by axis name.</summary>
    private static double Coord(MotionPosition p, string axis) => axis.ToUpperInvariant() switch
    {
        "X" => p.X,
        "Y" => p.Y,
        _ => p.Z,
    };
}
