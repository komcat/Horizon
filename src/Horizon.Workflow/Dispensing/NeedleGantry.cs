using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Dispensing;

/// <summary>
/// The gantry carrying the camera and the dispense needle, as the needle calibration and the
/// dispense procedure drive it (ported from FireDog's <c>move_axis_to</c> / <c>read_velocity</c>):
/// absolute moves in the device's <b>raw axis</b> coordinates, each at its own velocity.
///
/// <para><b>Velocity persists on the controller.</b> The descent runs at a crawl, and whatever
/// the last leg sets is what the next program step travels at — a head that appears not to move
/// for seconds, then a move timeout. So the velocity is read off the controller before the first
/// move (<see cref="SnapshotAsync"/>, refused when it can't be read: it could not be put back)
/// and written back by <see cref="RestoreAsync"/>. A leg with velocity ≤ 0 runs at that starting
/// velocity ("keep the device's speed").</para>
///
/// <para><b>Z direction is never assumed here</b> — callers pass absolute targets computed from a
/// measured sign. Camera and needle ride the same device, so nothing goes through a jog
/// transform.</para>
/// </summary>
internal sealed class NeedleGantry
{
    /// <summary>The facet's move wait times out at 60 s — refuse a leg that would need longer.</summary>
    private const double MaxLegSeconds = 55.0;

    private readonly IMotionFacet _motion;
    private readonly Action<LogLevel, string> _log;
    private readonly string _tag;
    private Dictionary<string, double>? _start;

    public NeedleGantry(IMotionFacet motion, string device, Action<LogLevel, string> log, string tag)
    {
        _motion = motion;
        Device = device;
        _log = log;
        _tag = tag;
    }

    public string Device { get; }

    /// <summary>Reads the device's velocity before anything here changes it. Must succeed before the first move.</summary>
    public async Task<Result> SnapshotAsync()
    {
        var info = _motion.GetDevice(Device);
        if (info == null)
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"Motion device '{Device}' not found.");
        bool isPi = info.ControllerType.Equals("PI", StringComparison.OrdinalIgnoreCase);
        var snap = await MoveApproach.ReadVelocitiesAsync(_motion, Device, isPi, info.Axes, _tag).ConfigureAwait(false);
        if (!snap.IsSuccess) return Result.Fail(snap.Error, snap.Message);
        _start = snap.Value;
        return Result.Ok();
    }

    /// <summary>Writes the starting velocity back. Never moves anything; safe on any exit path.</summary>
    public async Task<Result> RestoreAsync()
    {
        if (_start == null) return Result.Ok();
        var r = await MoveApproach.WriteVelocitiesAsync(_motion, Device, _start).ConfigureAwait(false);
        if (!r.IsSuccess) _log(LogLevel.Error, $"{_tag}: restoring the {Device} velocity FAILED — {r.Message}");
        return r;
    }

    /// <summary>The live pose, read from the controller.</summary>
    public async Task<Result<MotionPosition>> ReadAsync()
    {
        var r = await _motion.GetPositionAsync(Device).ConfigureAwait(false);
        return r.IsSuccess
            ? r
            : Result<MotionPosition>.Fail(r.Error, $"{_tag}: read {Device} position — {r.Message}");
    }

    /// <summary>Z alone to an absolute height at <paramref name="velocity"/> mm/s.</summary>
    public async Task<Result> MoveZAsync(double z, double velocity, CancellationToken ct)
    {
        var at = await ReadAsync().ConfigureAwait(false);
        if (!at.IsSuccess) return Result.Fail(at.Error, at.Message);
        var v = await ApplyVelocityAsync(Math.Abs(z - at.Value.Z), velocity).ConfigureAwait(false);
        if (!v.IsSuccess) return v;
        return await _motion.MoveAbsoluteAsync(Device, "Z", z, ct).ConfigureAwait(false);
    }

    /// <summary>One coordinated X/Y/Z move (other axes held) at <paramref name="velocity"/> mm/s.</summary>
    public async Task<Result> MoveXyzAsync(double x, double y, double z, double velocity, CancellationToken ct)
    {
        var at = await ReadAsync().ConfigureAwait(false);
        if (!at.IsSuccess) return Result.Fail(at.Error, at.Message);
        var p = at.Value;
        double dx = x - p.X, dy = y - p.Y, dz = z - p.Z;
        var v = await ApplyVelocityAsync(Math.Sqrt(dx * dx + dy * dy + dz * dz), velocity).ConfigureAwait(false);
        if (!v.IsSuccess) return v;
        return await _motion.MoveToPositionAsync(Device, new MotionPosition(x, y, z, p.U, p.V, p.W), ct)
            .ConfigureAwait(false);
    }

    /// <summary>X/Y only (Z held where it is) at <paramref name="velocity"/> mm/s.</summary>
    public async Task<Result> MoveXyAsync(double x, double y, double velocity, CancellationToken ct)
    {
        var at = await ReadAsync().ConfigureAwait(false);
        if (!at.IsSuccess) return Result.Fail(at.Error, at.Message);
        return await MoveXyzAsync(x, y, at.Value.Z, velocity, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="lift"/> mm from <paramref name="z"/> toward <paramref name="zUp"/>, never past
    /// it. <paramref name="zUp"/> is the camera's Z — where the needle came down from — so "toward it"
    /// is up whichever way the axis counts, even before anything has measured which way that is.
    /// </summary>
    public static double LiftToward(double z, double zUp, double lift)
    {
        double d = zUp - z;
        double s = d >= 0 ? 1.0 : -1.0;
        return z + s * Math.Min(Math.Abs(lift), Math.Abs(d));
    }

    /// <summary>Sets the leg velocity (≤ 0 = the starting velocity) and refuses a leg that would time out.</summary>
    private async Task<Result> ApplyVelocityAsync(double distanceMm, double velocity)
    {
        if (velocity <= 0)
        {
            // "Keep the device's speed" = the speed it had before this procedure touched it.
            if (_start == null) return Result.Ok();
            return await MoveApproach.WriteVelocitiesAsync(_motion, Device, _start).ConfigureAwait(false);
        }

        double v = MotionVelocityLimits.Clamp(velocity, _motion.GetDevice(Device)?.ControllerType);
        if (distanceMm / v > MaxLegSeconds)
            return Result.Fail(ErrorCode.InvalidParameter,
                $"{_tag}: {distanceMm:F3} mm at {v:G} mm/s needs {distanceMm / v:F0} s — over the {MaxLegSeconds:F0} s move limit. Raise the velocity.");
        var set = await _motion.SetDeviceVelocityAsync(Device, v).ConfigureAwait(false);
        return set.IsSuccess ? set : Result.Fail(set.Error, $"{_tag}: set {Device} velocity {v:G} mm/s — {set.Message}");
    }
}
