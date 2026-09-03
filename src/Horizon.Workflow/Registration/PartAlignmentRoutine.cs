using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Registration;

/// <summary>
/// Asks the operator to mark a point on the live feed (the page arms its Mark toggle and waits
/// for the click). Returns the marked point in the gantry frame, or null if cancelled. A
/// workflow step has no operator surface and passes no delegate — manual points then fail.
/// </summary>
public delegate Task<PartPoint?> PartManualMark(string label, CancellationToken ct);

/// <summary>
/// The maths and the moves behind a <see cref="PartAlignment"/>: the effective snapshot
/// (measured if a run happened, taught otherwise), the run that re-finds E1 / E2 and
/// re-measures every object, and the camera moves to a fiducial / an object (+ offset).
/// Camera moves here go to taught absolute positions (like a named position), so they are not
/// distance-bounded; detection converts an image point to the gantry frame the same way the
/// Run-page MoveClick does (mm/pixel + the MoveClick axis signs from app settings).
/// </summary>
internal sealed class PartAlignmentRoutine
{
    private readonly IHorizonEngine _engine;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string, Task<Result<VisionDetectResult>>> _runProfile;
    private readonly Action<LogLevel, string> _log;

    public PartAlignmentRoutine(IHorizonEngine engine, Func<AppSettings> settings,
        Func<string, Task<Result<VisionDetectResult>>> runProfile, Action<LogLevel, string> log)
    {
        _engine = engine;
        _settings = settings;
        _runProfile = runProfile;
        _log = log;
    }

    // --- snapshot (drawing + moves) ---

    /// <summary>The part as it stands now in the gantry frame — measured values where a run produced them, taught ones elsewhere.</summary>
    public static PartSnapshot Snapshot(PartAlignment part)
    {
        var m = part.Measured;
        var frame = part.EffectiveFrame;
        (double, double)? e1 = m != null ? (m.E1.X, m.E1.Y) : part.E1.Point is { } p1 ? (p1.X, p1.Y) : null;
        (double, double)? e2 = m != null ? (m.E2.X, m.E2.Y) : part.E2.Point is { } p2 ? (p2.X, p2.Y) : null;

        var objects = new List<PartSnapshotObject>();
        if (frame != null)
        {
            foreach (var obj in part.Objects)
            {
                if (!obj.IsComplete) continue;
                var measured = m?.Objects.FirstOrDefault(o => string.Equals(o.Name, obj.Name, StringComparison.OrdinalIgnoreCase));
                (double X, double Y)[] pts = measured is { Measured: true }
                    ? measured.Points.Select(p => (p.X, p.Y)).ToArray()
                    : obj.Points.Take(obj.RequiredPoints).Select(p => frame.ToGlobal(p.LocalX, p.LocalY)).ToArray();
                var reference = pts.Length == 2
                    ? ((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2)
                    : pts[0];
                var off = frame.VectorToGlobal(obj.OffsetX, obj.OffsetY);
                objects.Add(new PartSnapshotObject(obj.Name, obj.IsLine, measured is { Measured: true }, pts,
                    reference, (reference.Item1 + off.X, reference.Item2 + off.Y)));
            }
        }
        return new PartSnapshot(part.Name, e1, e2, frame, objects, m != null);
    }

    // --- detection ---

    /// <summary>
    /// Runs a vision profile on the live camera and converts its point (anchor preferred, x/y
    /// fallback) to the gantry frame at the camera's current pose.
    /// </summary>
    public async Task<Result<PartPoint>> DetectAsync(PartAlignment part, string profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
            return Result<PartPoint>.Fail(ErrorCode.InvalidParameter, "No vision profile set.");
        var det = await _runProfile(profile).ConfigureAwait(false);
        if (!det.IsSuccess) return Result<PartPoint>.Fail(det.Error, $"Profile '{profile}': {det.Message}");
        if (!det.Value.Found) return Result<PartPoint>.Fail(ErrorCode.OperationFailed, $"Profile '{profile}' found nothing.");

        double? x = Value(det.Value, "anchorX"), y = Value(det.Value, "anchorY");
        if (x is null || y is null) { x = Value(det.Value, "x"); y = Value(det.Value, "y"); }
        if (x is null || y is null)
            return Result<PartPoint>.Fail(ErrorCode.OperationFailed, $"Profile '{profile}' reports no x/y point (line / edge profile?).");

        var state = _engine.Camera.GetState(part.CameraId);
        if (state.Width <= 0 || state.Height <= 0)
            return Result<PartPoint>.Fail(ErrorCode.OperationFailed, "Camera frame size unknown — is it grabbing?");
        var pose = await _engine.Motion.GetPositionAsync(part.CameraDevice).ConfigureAwait(false);
        if (!pose.IsSuccess) return Result<PartPoint>.Fail(pose.Error, $"Read '{part.CameraDevice}' failed: {pose.Message}");

        return Result<PartPoint>.Ok(ToPartPoint(pose.Value, x.Value, y.Value, state.Width, state.Height, "vision:" + profile));

        static double? Value(VisionDetectResult r, string key)
        {
            foreach (var v in r.Values)
                if (string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)) return v.Value;
            return null;
        }
    }

    /// <summary>Image point (source px) at a camera pose → gantry-frame point, MoveClick semantics.</summary>
    public PartPoint ToPartPoint(MotionPosition camPose, double px, double py, int srcW, int srcH, string source)
    {
        var s = _settings();
        double sx = s.MoveClickXSign < 0 ? -1 : 1, sy = s.MoveClickYSign < 0 ? -1 : 1;
        double gx = camPose.X + (px - srcW / 2.0) * s.MmPerPixel * sx;
        double gy = camPose.Y + (py - srcH / 2.0) * s.MmPerPixel * sy;
        return new PartPoint(camPose, px, py, gx, gy, source);
    }

    // --- the run ---

    /// <summary>
    /// Re-finds E1 and E2 from their taught camera positions, rebuilds the frame, then re-measures
    /// every complete object from its taught local camera position through the new frame.
    /// An object whose detection fails is reported with its taught points through the new frame
    /// and <c>Measured = false</c>. Manual points need <paramref name="manual"/>.
    /// </summary>
    public async Task<Result<PartMeasurement>> RunAsync(PartAlignment part, PartManualMark? manual, CancellationToken ct)
    {
        if (part.E1.CameraPose == null || part.E2.CameraPose == null)
            return Result<PartMeasurement>.Fail(ErrorCode.InvalidParameter, "Teach the camera positions for E1 and E2 first.");
        if (!_engine.Motion.IsConnected(part.CameraDevice))
            return Result<PartMeasurement>.Fail(ErrorCode.OperationFailed, $"'{part.CameraDevice}' is not connected.");

        await ApplyExposureAsync(part).ConfigureAwait(false);

        var e1 = await FindAsync(part, "E1", part.E1.CameraPose, part.E1.VisionProfile, manual, ct).ConfigureAwait(false);
        if (!e1.IsSuccess) return Result<PartMeasurement>.Fail(e1.Error, e1.Message);
        var e2 = await FindAsync(part, "E2", part.E2.CameraPose, part.E2.VisionProfile, manual, ct).ConfigureAwait(false);
        if (!e2.IsSuccess) return Result<PartMeasurement>.Fail(e2.Error, e2.Message);

        var frame = PartFrame.From(e1.Value.X, e1.Value.Y, e2.Value.X, e2.Value.Y);
        _log(LogLevel.Info, $"Part '{part.Name}': frame origin ({frame.OriginX:F4}, {frame.OriginY:F4}), X axis {frame.AngleDeg:F3}°.");

        var results = new List<MeasuredPartObject>();
        foreach (var obj in part.Objects)
        {
            if (!obj.IsComplete) continue;
            ct.ThrowIfCancellationRequested();
            var pts = new List<PartPoint>();
            bool ok = true;
            for (int i = 0; i < obj.RequiredPoints; i++)
            {
                var tp = obj.Points[i];
                var (cx, cy) = frame.ToGlobal(tp.CamLocalX, tp.CamLocalY);
                var camPose = part.E1.CameraPose with { X = cx, Y = cy, Z = tp.CamZ };
                var found = await FindAsync(part, $"{obj.Name} P{i + 1}", camPose, tp.VisionProfile, manual, ct).ConfigureAwait(false);
                if (!found.IsSuccess)
                {
                    _log(LogLevel.Warning, $"Part '{part.Name}': object '{obj.Name}' point {i + 1} not measured — {found.Message}. Using the taught position.");
                    ok = false;
                    break;
                }
                pts.Add(found.Value);
            }

            if (!ok)
            {
                pts.Clear();
                foreach (var tp in obj.Points.Take(obj.RequiredPoints))
                {
                    var (gx, gy) = frame.ToGlobal(tp.LocalX, tp.LocalY);
                    var (cx, cy) = frame.ToGlobal(tp.CamLocalX, tp.CamLocalY);
                    pts.Add(new PartPoint(part.E1.CameraPose with { X = cx, Y = cy, Z = tp.CamZ }, tp.PixelX, tp.PixelY, gx, gy, "taught"));
                }
            }
            results.Add(new MeasuredPartObject(obj.Name, pts.ToArray(), ok));
        }

        return Result<PartMeasurement>.Ok(new PartMeasurement(e1.Value, e2.Value, frame, results.ToArray(), DateTimeOffset.Now.ToString("o")));
    }

    /// <summary>Move the camera to <paramref name="camPose"/>, settle, then detect with the profile or ask the operator.</summary>
    private async Task<Result<PartPoint>> FindAsync(PartAlignment part, string label, MotionPosition camPose, string profile, PartManualMark? manual, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var mv = await _engine.Motion.MoveToPositionAsync(part.CameraDevice, camPose, ct).ConfigureAwait(false);
        if (!mv.IsSuccess) return Result<PartPoint>.Fail(mv.Error, $"{label}: camera move failed — {mv.Message}");
        if (part.SettleMs > 0) await Task.Delay(Math.Min(part.SettleMs, 60_000), ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(profile))
        {
            var r = await DetectAsync(part, profile).ConfigureAwait(false);
            if (r.IsSuccess) _log(LogLevel.Info, $"Part '{part.Name}': {label} detected by '{profile}' at ({r.Value.X:F4}, {r.Value.Y:F4}).");
            return r.IsSuccess ? r : Result<PartPoint>.Fail(r.Error, $"{label}: {r.Message}");
        }

        if (manual == null)
            return Result<PartPoint>.Fail(ErrorCode.InvalidParameter, $"{label} is marked manually, which needs the Part Alignment page — set a vision profile to run it from a program.");
        var pt = await manual(label, ct).ConfigureAwait(false);
        if (pt == null) return Result<PartPoint>.Fail(ErrorCode.OperationFailed, $"{label}: manual mark cancelled.");
        _log(LogLevel.Info, $"Part '{part.Name}': {label} marked manually at ({pt.X:F4}, {pt.Y:F4}).");
        return Result<PartPoint>.Ok(pt);
    }

    private async Task ApplyExposureAsync(PartAlignment part)
    {
        if (!_engine.Camera.IsConnected(part.CameraId)) return;
        var cur = _engine.Camera.GetState(part.CameraId).Exposure;
        var r = await _engine.Camera.ApplyExposureAsync(part.CameraId,
            cur with { AutoExposure = part.AutoExposure, ExposureUs = part.ExposureUs, Gain = part.Gain }).ConfigureAwait(false);
        if (!r.IsSuccess) _log(LogLevel.Warning, $"Part '{part.Name}': exposure apply failed — {r.Message}");
    }

    // --- camera moves ---

    /// <summary>Camera → fiducial 1 or 2 (effective XY; Z from the taught camera position).</summary>
    public async Task<Result> MoveCameraToFiducialAsync(PartAlignment part, int which, CancellationToken ct)
    {
        var snap = Snapshot(part);
        var target = which == 2 ? snap.E2 : snap.E1;
        var taught = which == 2 ? part.E2.CameraPose : part.E1.CameraPose;
        if (target is not { } t || taught == null)
            return Result.Fail(ErrorCode.InvalidParameter, $"E{which} of part '{part.Name}' is not taught.");
        var cur = await _engine.Motion.GetPositionAsync(part.CameraDevice).ConfigureAwait(false);
        if (!cur.IsSuccess) return Result.Fail(cur.Error, $"Read '{part.CameraDevice}' failed: {cur.Message}");
        var pose = cur.Value with { X = t.X, Y = t.Y, Z = taught.Z };
        _log(LogLevel.Info, $"Camera → E{which} of '{part.Name}' ({t.X:F4}, {t.Y:F4}, Z {taught.Z:F4}){(snap.FromMeasurement ? " [measured]" : " [taught]")}.");
        return await _engine.Motion.MoveToPositionAsync(part.CameraDevice, pose, ct).ConfigureAwait(false);
    }

    /// <summary>Camera → a local object's reference point (+ its local-frame offset when asked); Z = the taught points' mean camera Z.</summary>
    public async Task<Result> MoveCameraToObjectAsync(PartAlignment part, string objectName, bool applyOffset, CancellationToken ct)
    {
        var obj = part.Objects.FirstOrDefault(o => string.Equals(o.Name, objectName, StringComparison.OrdinalIgnoreCase));
        if (obj == null || !obj.IsComplete)
            return Result.Fail(ErrorCode.InvalidParameter, $"Object '{objectName}' of part '{part.Name}' is not taught.");
        var snap = Snapshot(part);
        var so = snap.Objects.FirstOrDefault(o => string.Equals(o.Name, objectName, StringComparison.OrdinalIgnoreCase));
        if (so == null)
            return Result.Fail(ErrorCode.InvalidParameter, $"Part '{part.Name}' has no frame yet — teach / run E1 and E2 first.");
        var (tx, ty) = applyOffset ? so.Target : so.Reference;
        double z = obj.Points.Take(obj.RequiredPoints).Average(p => p.CamZ);
        var cur = await _engine.Motion.GetPositionAsync(part.CameraDevice).ConfigureAwait(false);
        if (!cur.IsSuccess) return Result.Fail(cur.Error, $"Read '{part.CameraDevice}' failed: {cur.Message}");
        var pose = cur.Value with { X = tx, Y = ty, Z = z };
        _log(LogLevel.Info, $"Camera → '{objectName}' of '{part.Name}'{(applyOffset ? $" + offset ({obj.OffsetX:F3}, {obj.OffsetY:F3})" : "")} → ({tx:F4}, {ty:F4}, Z {z:F4}){(so.Measured ? " [measured]" : " [taught]")}.");
        return await _engine.Motion.MoveToPositionAsync(part.CameraDevice, pose, ct).ConfigureAwait(false);
    }
}
