using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Registration;

/// <summary>
/// The maths and the moves behind a <see cref="ToolRegistration"/>:
/// <list type="bullet">
/// <item><see cref="Locate"/> — where the tool is right now in the camera device's frame
///   (from cached motion states; cheap enough for a live overlay);</item>
/// <item><see cref="MoveCameraToToolAsync"/> — send the camera device so the camera is centred
///   on the tool (camera device XY, plus Z to the focus height when asked);</item>
/// <item><see cref="MoveToolToCameraAsync"/> — send the tool device so the tool sits under the
///   camera centre (the mapped tool axes only, plus the Z-mapped axis when asked);</item>
/// <item><see cref="MoveToolToPoseAsync"/> — send the tool device back to its registered pose.</item>
/// </list>
/// The camera/tool moves are bounded: a required travel longer than the registration's
/// <see cref="ToolRegistration.MaxMoveMm"/> (or a tighter caller limit) is <b>refused</b>, never
/// clamped — a clamped move would park the device somewhere nobody asked for.
/// </summary>
internal sealed class ToolRegistrationRoutine
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;

    public ToolRegistrationRoutine(IHorizonEngine engine, Action<LogLevel, string> log)
    {
        _engine = engine;
        _log = log;
    }

    /// <summary>Projects a tool pose into the camera device's frame using the registration's axis maps (Z null without a focus Z).</summary>
    public static (double X, double Y, double? Z) ToolCentreAt(ToolRegistration reg, MotionPosition toolPose)
    {
        var t0 = reg.ToolPose ?? toolPose;
        double dx = reg.MapX.Sign * (Axis(toolPose, reg.MapX.ToolAxis) - Axis(t0, reg.MapX.ToolAxis));
        double dy = reg.MapY.Sign * (Axis(toolPose, reg.MapY.ToolAxis) - Axis(t0, reg.MapY.ToolAxis));
        double? z = null;
        if (reg.HasZ)
            z = reg.CameraZ!.Value + reg.MapZ.Sign * (Axis(toolPose, reg.MapZ.ToolAxis) - Axis(t0, reg.MapZ.ToolAxis));
        return (reg.ToolX + dx, reg.ToolY + dy, z);
    }

    /// <summary>
    /// Live tool location from the engine's cached (polled) motion states. Fails when the
    /// registration is incomplete or either device has not reported a valid state yet.
    /// </summary>
    public Result<ToolLocation> Locate(ToolRegistration reg)
    {
        var check = Validate(reg);
        if (!check.IsSuccess) return Result<ToolLocation>.Fail(ErrorCode.InvalidParameter, check.Message);

        var tool = _engine.Motion.GetState(reg.ToolDevice);
        var cam = _engine.Motion.GetState(reg.CameraDevice);
        if (!tool.Valid || !cam.Valid)
            return Result<ToolLocation>.Fail(ErrorCode.OperationFailed,
                $"No motion state yet for '{(tool.Valid ? reg.CameraDevice : reg.ToolDevice)}'.");

        return Result<ToolLocation>.Ok(Build(reg, tool.Position, cam.Position));
    }

    /// <summary>
    /// Live location of a gripped object (cached states): its taught centre shifted by the
    /// tool's move since the object's own tool pose, through the tool registration's axis
    /// maps. Z (the object's focus height) follows the same way when both the registration and
    /// the object know their Z.
    /// </summary>
    public Result<ToolLocation> LocateObject(ToolRegistration reg, GrippedObject obj)
    {
        var check = Validate(reg);
        if (!check.IsSuccess) return Result<ToolLocation>.Fail(ErrorCode.InvalidParameter, check.Message);
        if (obj.ToolPose == null)
            return Result<ToolLocation>.Fail(ErrorCode.InvalidParameter, $"Gripped object '{obj.Name}' has no tool pose.");

        var tool = _engine.Motion.GetState(reg.ToolDevice);
        var cam = _engine.Motion.GetState(reg.CameraDevice);
        if (!tool.Valid || !cam.Valid)
            return Result<ToolLocation>.Fail(ErrorCode.OperationFailed,
                $"No motion state yet for '{(tool.Valid ? reg.CameraDevice : reg.ToolDevice)}'.");

        var now = ToolCentreAt(reg, tool.Position);
        var then = ToolCentreAt(reg, obj.ToolPose);
        double x = obj.CentreX + (now.X - then.X);
        double y = obj.CentreY + (now.Y - then.Y);
        double? z = obj.FocusZ is { } fz && now.Z is { } nz && then.Z is { } tz ? fz + (nz - tz) : null;
        return Result<ToolLocation>.Ok(new ToolLocation(obj.Name, x, y, cam.Position.X, cam.Position.Y,
            x - cam.Position.X, y - cam.Position.Y, z, cam.Position.Z, z is { } zz ? zz - cam.Position.Z : 0));
    }

    /// <summary>
    /// Moves the camera device so the camera is centred on the tool. Reads both devices live,
    /// computes the delta, refuses if it exceeds <paramref name="maxDistanceMm"/> (0 = the
    /// registration's own bound), then issues one coordinated move. With
    /// <paramref name="includeZ"/> the camera also goes to the focus height (needs a captured
    /// focus Z and the bound covers XYZ); otherwise Z/U/V/W are unchanged.
    /// </summary>
    public async Task<Result> MoveCameraToToolAsync(ToolRegistration reg, double maxDistanceMm, bool includeZ, CancellationToken ct)
    {
        var live = await ReadLiveAsync(reg).ConfigureAwait(false);
        if (!live.IsSuccess) return Result.Fail(ErrorCode.OperationFailed, live.Message);
        var (loc, _, camPose) = live.Value;

        if (includeZ && !loc.HasZ)
            return Result.Fail(ErrorCode.InvalidParameter, $"Camera → {reg.Label}: no focus Z captured for '{reg.ToolDevice}' — capture it on the Registration section, or move XY only.");

        double bound = Bound(reg, maxDistanceMm);
        double dist = includeZ ? loc.Distance3Mm : loc.DistanceMm;
        if (dist > bound)
            return Result.Fail(ErrorCode.InvalidParameter,
                $"Camera → {reg.Label}: required move {dist:F3} mm exceeds the {bound:F1} mm bound — refused.");

        var target = camPose with { X = loc.ToolX, Y = loc.ToolY };
        if (includeZ) target = target with { Z = loc.ToolZ!.Value };
        _log(LogLevel.Info,
            $"Camera → {reg.Label}: '{reg.CameraDevice}' ({camPose.X:F4}, {camPose.Y:F4}, {camPose.Z:F4}) → ({target.X:F4}, {target.Y:F4}, {target.Z:F4}), " +
            $"Δ ({loc.DxMm:+0.000;-0.000}, {loc.DyMm:+0.000;-0.000}{(includeZ ? $", {loc.DzMm:+0.000;-0.000}" : "")}) mm.");
        return await _engine.Motion.MoveToPositionAsync(reg.CameraDevice, target, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the tool device so the tool sits where the camera is centred. Only the tool axes
    /// named by the registration's maps move (the others keep their live values); the
    /// camera-frame delta is inverted through the ±1 signs. With <paramref name="includeZ"/>
    /// the Z-mapped axis also moves so the tool reaches the camera's current focus plane plus
    /// <paramref name="zOffsetMm"/> (null = the registration's stored offset; negative = higher
    /// on the gantry's Z convention). Same bound rule as the camera move.
    /// </summary>
    public async Task<Result> MoveToolToCameraAsync(ToolRegistration reg, double maxDistanceMm, bool includeZ, double? zOffsetMm, CancellationToken ct)
    {
        var live = await ReadLiveAsync(reg).ConfigureAwait(false);
        if (!live.IsSuccess) return Result.Fail(ErrorCode.OperationFailed, live.Message);
        var (loc, toolPose, _) = live.Value;

        if (includeZ && !loc.HasZ)
            return Result.Fail(ErrorCode.InvalidParameter, $"{reg.Label} → camera: no focus Z captured for '{reg.ToolDevice}' — capture it on the Registration section, or move XY only.");
        if (includeZ && (string.Equals(reg.MapZ.ToolAxis, reg.MapX.ToolAxis, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(reg.MapZ.ToolAxis, reg.MapY.ToolAxis, StringComparison.OrdinalIgnoreCase)))
            return Result.Fail(ErrorCode.InvalidParameter, $"{reg.Label} → camera: the Z axis map reuses an XY tool axis — fix the registration.");

        // Camera-frame delta needed is (camera − tool) = −(Dx, Dy); in Z the target is the focus
        // plane plus the offset, so the needed Z delta is (offset − Dz). ±1 signs invert by multiplying.
        double zOffset = zOffsetMm ?? reg.ToolToCameraZOffsetMm;
        double needZ = includeZ ? zOffset - loc.DzMm : 0;

        double bound = Bound(reg, maxDistanceMm);
        double dist = Math.Sqrt(loc.DxMm * loc.DxMm + loc.DyMm * loc.DyMm + needZ * needZ);
        if (dist > bound)
            return Result.Fail(ErrorCode.InvalidParameter,
                $"{reg.Label} → camera: required move {dist:F3} mm exceeds the {bound:F1} mm bound — refused.");

        double dAxisX = -loc.DxMm * reg.MapX.Sign;
        double dAxisY = -loc.DyMm * reg.MapY.Sign;
        var target = WithAxis(WithAxis(toolPose, reg.MapX.ToolAxis, dAxisX), reg.MapY.ToolAxis, dAxisY);
        double dAxisZ = 0;
        if (includeZ)
        {
            dAxisZ = needZ * reg.MapZ.Sign;
            target = WithAxis(target, reg.MapZ.ToolAxis, dAxisZ);
        }

        _log(LogLevel.Info,
            $"{reg.Label} → camera: '{reg.ToolDevice}' {reg.MapX.ToolAxis} {dAxisX:+0.0000;-0.0000}, " +
            $"{reg.MapY.ToolAxis} {dAxisY:+0.0000;-0.0000}" +
            (includeZ ? $", {reg.MapZ.ToolAxis} {dAxisZ:+0.0000;-0.0000} (Z offset {zOffset:+0.000;-0.000})" : "") +
            $" mm (camera-frame Δ {dist:F3} mm).");
        return await _engine.Motion.MoveToPositionAsync(reg.ToolDevice, target, ct).ConfigureAwait(false);
    }

    /// <summary>Moves the tool device back to its registered pose (all six axes, one coordinated move).</summary>
    public async Task<Result> MoveToolToPoseAsync(ToolRegistration reg, CancellationToken ct)
    {
        if (reg.ToolPose == null)
            return Result.Fail(ErrorCode.InvalidParameter, $"'{reg.ToolDevice}' has no registered pose.");
        if (!_engine.Motion.IsConnected(reg.ToolDevice))
            return Result.Fail(ErrorCode.OperationFailed, $"'{reg.ToolDevice}' is not connected.");

        var p = reg.ToolPose;
        _log(LogLevel.Info,
            $"{reg.Label} → pose: '{reg.ToolDevice}' to ({p.X:F4}, {p.Y:F4}, {p.Z:F4}, {p.U:F4}, {p.V:F4}, {p.W:F4}).");
        return await _engine.Motion.MoveToPositionAsync(reg.ToolDevice, p, ct).ConfigureAwait(false);
    }

    // --- helpers ---

    private async Task<Result<(ToolLocation Loc, MotionPosition Tool, MotionPosition Cam)>> ReadLiveAsync(ToolRegistration reg)
    {
        var check = Validate(reg);
        if (!check.IsSuccess)
            return Result<(ToolLocation, MotionPosition, MotionPosition)>.Fail(ErrorCode.InvalidParameter, check.Message);
        if (!_engine.Motion.IsConnected(reg.ToolDevice))
            return Result<(ToolLocation, MotionPosition, MotionPosition)>.Fail(ErrorCode.OperationFailed, $"'{reg.ToolDevice}' is not connected.");
        if (!_engine.Motion.IsConnected(reg.CameraDevice))
            return Result<(ToolLocation, MotionPosition, MotionPosition)>.Fail(ErrorCode.OperationFailed, $"'{reg.CameraDevice}' is not connected.");

        var tool = await _engine.Motion.GetPositionAsync(reg.ToolDevice).ConfigureAwait(false);
        if (!tool.IsSuccess)
            return Result<(ToolLocation, MotionPosition, MotionPosition)>.Fail(ErrorCode.OperationFailed, $"Read '{reg.ToolDevice}' failed: {tool.Message}");
        var cam = await _engine.Motion.GetPositionAsync(reg.CameraDevice).ConfigureAwait(false);
        if (!cam.IsSuccess)
            return Result<(ToolLocation, MotionPosition, MotionPosition)>.Fail(ErrorCode.OperationFailed, $"Read '{reg.CameraDevice}' failed: {cam.Message}");

        return Result<(ToolLocation, MotionPosition, MotionPosition)>.Ok((Build(reg, tool.Value, cam.Value), tool.Value, cam.Value));
    }

    private static ToolLocation Build(ToolRegistration reg, MotionPosition toolPose, MotionPosition camPose)
    {
        var (tx, ty, tz) = ToolCentreAt(reg, toolPose);
        return new ToolLocation(reg.ToolDevice, tx, ty, camPose.X, camPose.Y, tx - camPose.X, ty - camPose.Y,
            tz, camPose.Z, tz is { } z ? z - camPose.Z : 0);
    }

    private static Result Validate(ToolRegistration reg)
    {
        if (reg.ToolPose == null)
            return Result.Fail(ErrorCode.InvalidParameter, $"'{reg.ToolDevice}' is not registered yet (no pose saved).");
        if (!reg.MapX.IsValid || !reg.MapY.IsValid)
            return Result.Fail(ErrorCode.InvalidParameter, $"'{reg.ToolDevice}' registration has an invalid axis map (need a single axis X..W with sign ±1).");
        if (string.Equals(reg.MapX.ToolAxis, reg.MapY.ToolAxis, StringComparison.OrdinalIgnoreCase))
            return Result.Fail(ErrorCode.InvalidParameter, $"'{reg.ToolDevice}' registration maps camera X and Y to the same tool axis.");
        if (reg.MaxMoveMm <= 0)
            return Result.Fail(ErrorCode.InvalidParameter, $"'{reg.ToolDevice}' registration has a non-positive move bound.");
        return Result.Ok();
    }

    /// <summary>The effective bound: the registration's, tightened by a positive caller limit.</summary>
    private static double Bound(ToolRegistration reg, double callerMax)
        => callerMax > 0 ? Math.Min(callerMax, reg.MaxMoveMm) : reg.MaxMoveMm;

    private static double Axis(MotionPosition p, string axis) => axis.Trim().ToUpperInvariant() switch
    {
        "X" => p.X, "Y" => p.Y, "Z" => p.Z, "U" => p.U, "V" => p.V, "W" => p.W,
        _ => 0,
    };

    private static MotionPosition WithAxis(MotionPosition p, string axis, double delta) => axis.Trim().ToUpperInvariant() switch
    {
        "X" => p with { X = p.X + delta },
        "Y" => p with { Y = p.Y + delta },
        "Z" => p with { Z = p.Z + delta },
        "U" => p with { U = p.U + delta },
        "V" => p with { V = p.V + delta },
        "W" => p with { W = p.W + delta },
        _ => p,
    };
}
