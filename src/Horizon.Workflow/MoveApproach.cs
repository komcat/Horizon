using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// The approach phase of a named-position move (<see cref="MoveOverrides.ApproachDistance"/> /
/// <see cref="MoveOverrides.ApproachVelocity"/>): the device travels at its normal velocity to a
/// point <c>ApproachDistance</c> mm short of the target — on the straight line from where it
/// starts, measured on X/Y/Z (U/V/W follow proportionally) — then finishes at
/// <c>ApproachVelocity</c>, and its velocity is put back to what it was before the slow leg:
/// always, also on STOP, a move fault or a run cancel. A move that starts within the approach
/// distance runs slow all the way. Refuses (no velocity change, no motion) when the velocity to
/// restore can't be read or the slow leg would outlast the move timeout.
/// </summary>
internal static class MoveApproach
{
    /// <summary>The facet's move wait times out at 60 s — refuse a slow leg that would need longer.</summary>
    public const double MaxSlowLegSeconds = 55.0;

    /// <summary>The device must stand within this of the approach point (mm / deg, every axis) before the slow leg starts.</summary>
    public const double ApproachPointTolerance = 0.01;

    /// <summary>Key used in a velocity snapshot for a PI device's single system (path) velocity.</summary>
    private const string SystemKey = "system";

    /// <summary>Both approach fields are set (0 = not set).</summary>
    public static bool IsSet(MoveOverrides o) => o.ApproachDistance > 0 && o.ApproachVelocity > 0;

    /// <summary>
    /// Moves <paramref name="device"/> to <paramref name="target"/> with the approach phase. The
    /// device's normal velocity must already be applied — it is what the fast leg runs at and
    /// what is restored afterwards.
    /// </summary>
    public static async Task<Result> RunAsync(
        IMotionFacet motion, string device, MotionPosition target, double distanceMm, double approachVelocity,
        Action<LogLevel, string> log, CancellationToken ct)
    {
        var info = motion.GetDevice(device);
        if (info == null)
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{device}' not found.");
        bool isPi = info.ControllerType.Equals("PI", StringComparison.OrdinalIgnoreCase);
        double slow = MotionVelocityLimits.Clamp(approachVelocity, info.ControllerType);

        var start = await motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!start.IsSuccess) return Result.Fail(start.Error, $"Approach {device}: read position — {start.Message}");

        double length = LinearDistance(start.Value, target);
        double slowMm = Math.Min(distanceMm, length);
        if (slowMm / slow > MaxSlowLegSeconds)
            return Result.Fail(ErrorCode.InvalidParameter,
                $"Approach {device}: {slowMm:F3} mm at {slow:G} mm/s needs {slowMm / slow:F0} s — over the {MaxSlowLegSeconds:F0} s move limit.");

        // Snapshot BEFORE touching anything — a velocity that can't be read could not be restored.
        var snapshot = await ReadVelocitiesAsync(motion, device, isPi, info.Axes).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result.Fail(snapshot.Error, snapshot.Message);

        // 1) Fast leg to the approach point (skipped when the move starts inside the approach distance).
        if (length > distanceMm)
        {
            var point = Lerp(start.Value, target, 1.0 - distanceMm / length);
            var fast = await motion.MoveToPositionAsync(device, point, ct).ConfigureAwait(false);
            if (!fast.IsSuccess) return fast;

            // A STOP mid-move also ends the move "successfully" (motion simply ceased) — never
            // start the slow leg unless the device really stands on the approach point.
            var at = await motion.GetPositionAsync(device).ConfigureAwait(false);
            if (!at.IsSuccess)
                return Result.Fail(at.Error, $"Approach {device}: confirm approach point — {at.Message}");
            if (!Near(at.Value, point, info.Axes))
                return Result.Fail(ErrorCode.MotionAxisError,
                    $"Approach {device}: stopped short of the approach point — slow leg not started.");
        }

        log(LogLevel.Info, $"Approach {device}: last {slowMm:F3} mm at {slow:G} mm/s (velocity snapshot " +
            string.Join(", ", snapshot.Value.Select(kv => $"{kv.Key}={kv.Value:G}")) + ").");

        // 2) Slow leg. The snapshot is always written back — STOP, a fault and a run cancel all land in finally.
        Result slowLeg;
        Result restored;
        try
        {
            var set = await motion.SetDeviceVelocityAsync(device, slow).ConfigureAwait(false);
            slowLeg = set.IsSuccess
                ? await motion.MoveToPositionAsync(device, target, ct).ConfigureAwait(false)
                : Result.Fail(set.Error, $"Approach {device}: set {slow:G} mm/s — {set.Message}");
        }
        finally
        {
            restored = await WriteVelocitiesAsync(motion, device, snapshot.Value).ConfigureAwait(false);
            log(restored.IsSuccess ? LogLevel.Info : LogLevel.Error, restored.IsSuccess
                ? $"Approach {device}: velocity restored."
                : $"Approach {device}: velocity restore FAILED — {restored.Message}");
        }

        if (!slowLeg.IsSuccess) return slowLeg;
        return restored.IsSuccess
            ? Result.Ok()
            : Result.Fail(restored.Error, $"Approach {device}: arrived, but restoring the velocity failed — {restored.Message}");
    }

    /// <summary>Straight-line X/Y/Z distance (mm) between two poses.</summary>
    private static double LinearDistance(MotionPosition a, MotionPosition b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The pose a fraction <paramref name="f"/> of the way from <paramref name="a"/> to <paramref name="b"/>, every axis.</summary>
    private static MotionPosition Lerp(MotionPosition a, MotionPosition b, double f)
        => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f,
               a.U + (b.U - a.U) * f, a.V + (b.V - a.V) * f, a.W + (b.W - a.W) * f);

    private static bool Near(MotionPosition live, MotionPosition point, IReadOnlyList<string> axes)
        => axes.All(axis => Math.Abs(Coord(live, axis) - Coord(point, axis)) <= ApproachPointTolerance);

    private static double Coord(MotionPosition p, string axis) => axis.Trim().ToUpperInvariant() switch
    {
        "X" => p.X,
        "Y" => p.Y,
        "Z" => p.Z,
        "U" => p.U,
        "V" => p.V,
        "W" => p.W,
        _ => 0,
    };

    /// <summary>The device's current velocity — PI: the system velocity; others: every installed axis. A read of 0 counts as a failure.</summary>
    internal static async Task<Result<Dictionary<string, double>>> ReadVelocitiesAsync(
        IMotionFacet motion, string device, bool isPi, IReadOnlyList<string> axes, string what = "Approach")
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (isPi)
        {
            var r = await motion.GetSystemVelocityAsync(device).ConfigureAwait(false);
            if (!r.IsSuccess || r.Value <= 0)
                return Result<Dictionary<string, double>>.Fail(ErrorCode.MotionCommunicationError,
                    $"{what} {device}: could not read the system velocity — refused (it could not be restored).");
            map[SystemKey] = r.Value;
            return Result<Dictionary<string, double>>.Ok(map);
        }

        foreach (var axis in axes)
        {
            var r = await motion.GetVelocityAsync(device, axis).ConfigureAwait(false);
            if (!r.IsSuccess || r.Value <= 0)
                return Result<Dictionary<string, double>>.Fail(ErrorCode.MotionCommunicationError,
                    $"{what} {device}: could not read the {axis} velocity — refused (it could not be restored).");
            map[axis] = r.Value;
        }
        return Result<Dictionary<string, double>>.Ok(map);
    }

    /// <summary>Writes a velocity snapshot back; tries every entry, reports the first failure.</summary>
    internal static async Task<Result> WriteVelocitiesAsync(
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
}
