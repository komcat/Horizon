using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// The run sequence of a <c>vision.detectObject</c> block: go to the block's own camera position,
/// apply the recipe's exposure + lighting, detect, optionally fall back to an operator mark, then
/// optionally centre the camera on the result (guarded — refused, never clamped) and step off by
/// an offset. The camera rides on <see cref="AppSettings.MoveClickDevice"/> (gantry-main).
/// A velocity the block sets is restored on every exit. See <c>docs/detect-object-design.md</c>.
/// </summary>
internal sealed class DetectObjectRoutine
{
    /// <summary>The device must stand within this (mm) of a commanded point before the routine carries on.</summary>
    private const double ArrivalToleranceMm = 0.01;
    private const int MaxSettleMs = 60_000;
    private const int FrameWaitMs = 2000;

    private readonly IHorizonEngine _engine;
    private readonly Func<AppSettings> _settings;
    private readonly Func<VisionProfile, Task<Result<VisionDetectResult>>> _detect;
    private readonly Func<IOperatorPrompt?> _prompt;
    private readonly Action<LogLevel, string> _log;

    public DetectObjectRoutine(
        IHorizonEngine engine,
        Func<AppSettings> settings,
        Func<VisionProfile, Task<Result<VisionDetectResult>>> detect,
        Func<IOperatorPrompt?> prompt,
        Action<LogLevel, string> log)
    {
        _engine = engine;
        _settings = settings;
        _detect = detect;
        _prompt = prompt;
        _log = log;
    }

    /// <summary>
    /// Runs the block. <paramref name="onResult"/> is called once with the detection outcome
    /// (found or not) <b>before</b> any move to the result, so the program's variables are set
    /// even when that move is refused.
    /// </summary>
    public async Task<Result<DetectedObject>> RunAsync(
        DetectObjectRequest req, Action<DetectedObject> onResult, CancellationToken ct)
    {
        var settings = _settings();
        string device = string.IsNullOrWhiteSpace(settings.MoveClickDevice) ? "gantry-main" : settings.MoveClickDevice;

        var profile = DetectObjectRecipe.Parse(req.Recipe);
        if (profile == null)
            return Fail(ErrorCode.InvalidParameter, req, "no vision recipe — open the Vision helper on the block and save one.");
        if (string.IsNullOrWhiteSpace(profile.CameraId) || !_engine.Camera.IsConnected(profile.CameraId))
            return Fail(ErrorCode.OperationFailed, req, $"camera '{profile.CameraId}' is not connected.");
        if (!IsFinite(req.OffsetX, req.OffsetY, req.OffsetZ, req.Velocity, req.MaxMoveMm))
            return Fail(ErrorCode.InvalidParameter, req, "a numeric parameter is not a number.");

        return await WithVelocityAsync(device, req.Velocity, req.Label,
            () => RunCoreAsync(req, profile, settings, device, onResult, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// The editor's <b>Go</b>: moves the camera device to a block's own position (X/Y/Z, other axes
    /// untouched) at <paramref name="velocity"/> (0 = as it is), restoring the velocity afterwards.
    /// </summary>
    public async Task<Result> MoveToBlockPositionAsync(double x, double y, double z, double velocity, CancellationToken ct)
    {
        if (!IsFinite(x, y, z, velocity))
            return Result.Fail(ErrorCode.InvalidParameter, "Detect Object: the block position is not a number.");
        var s = _settings();
        string device = string.IsNullOrWhiteSpace(s.MoveClickDevice) ? "gantry-main" : s.MoveClickDevice;
        var req = new DetectObjectRequest { Label = "Detect Object" };

        var r = await WithVelocityAsync(device, velocity, req.Label, async () =>
        {
            var here = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
            if (!here.IsSuccess) return Fail(here.Error, req, $"read {device} — {here.Message}");
            var moved = await MoveAndConfirmAsync(device, here.Value with { X = x, Y = y, Z = z }, "block position", req, ct).ConfigureAwait(false);
            return moved.IsSuccess
                ? Result<DetectedObject>.Ok(DetectedObject.NotFound)
                : Result<DetectedObject>.Fail(moved.Error, moved.Message);
        }).ConfigureAwait(false);
        return r.IsSuccess ? Result.Ok() : Result.Fail(r.Error, r.Message);
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the device at <paramref name="velocity"/> (0 = untouched).
    /// The velocity is snapshotted first — one that can't be read could not be restored, so the
    /// run is refused — and written back on every exit.
    /// </summary>
    private async Task<Result<DetectedObject>> WithVelocityAsync(
        string device, double velocity, string label, Func<Task<Result<DetectedObject>>> body)
    {
        var info = _engine.Motion.GetDevice(device);
        if (info == null)
            return Result<DetectedObject>.Fail(ErrorCode.MotionDeviceNotFound, $"{label}: motion device '{device}' not found.");

        Dictionary<string, double>? snapshot = null;
        if (velocity > 0)
        {
            bool isPi = info.ControllerType.Equals("PI", StringComparison.OrdinalIgnoreCase);
            var read = await MoveApproach.ReadVelocitiesAsync(_engine.Motion, device, isPi, info.Axes, label).ConfigureAwait(false);
            if (!read.IsSuccess) return Result<DetectedObject>.Fail(read.Error, read.Message);
            snapshot = read.Value;
        }

        try
        {
            if (snapshot != null)
            {
                var set = await _engine.Motion.SetDeviceVelocityAsync(
                    device, MotionVelocityLimits.Clamp(velocity, info.ControllerType)).ConfigureAwait(false);
                if (!set.IsSuccess)
                    return Result<DetectedObject>.Fail(set.Error, $"{label}: set velocity — {set.Message}");
            }
            return await body().ConfigureAwait(false);
        }
        finally
        {
            if (snapshot != null)
            {
                var restored = await MoveApproach.WriteVelocitiesAsync(_engine.Motion, device, snapshot).ConfigureAwait(false);
                if (!restored.IsSuccess)
                    _log(LogLevel.Warning, $"{label}: could not restore the {device} velocity — {restored.Message}");
            }
        }
    }

    private async Task<Result<DetectedObject>> RunCoreAsync(
        DetectObjectRequest req, VisionProfile profile, AppSettings settings, string device,
        Action<DetectedObject> onResult, CancellationToken ct)
    {
        string camera = profile.CameraId;

        // 1) The block's own camera position.
        if (req.HasPosition)
        {
            var here = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
            if (!here.IsSuccess) return Fail(here.Error, req, $"read {device} — {here.Message}");
            var target = here.Value with { X = req.PosX!.Value, Y = req.PosY!.Value, Z = req.PosZ!.Value };
            _log(LogLevel.Info, $"{req.Label}: {device} → block position ({target.X:F4}, {target.Y:F4}, {target.Z:F4}).");
            var moved = await MoveAndConfirmAsync(device, target, "block position", req, ct).ConfigureAwait(false);
            if (!moved.IsSuccess) return Result<DetectedObject>.Fail(moved.Error, moved.Message);
        }

        // 2) Lighting (exposure is applied by the detection call). Best-effort, like the Vision Profile page.
        if (!string.IsNullOrEmpty(profile.LightingDevice) && _engine.Lighting.DeviceNames.Contains(profile.LightingDevice))
        {
            var lit = await _engine.Lighting.SetAllAsync(profile.LightingDevice, profile.LightingIntensity).ConfigureAwait(false);
            if (!lit.IsSuccess)
                _log(LogLevel.Warning, $"{req.Label}: lighting {profile.LightingDevice} failed — {lit.Message}");
        }

        // 3) Hold a grab lease for the whole look, so there is a live frame whoever else is watching.
        var lease = await _engine.Camera.StartGrabbingAsync(camera).ConfigureAwait(false);
        try
        {
            int settle = Math.Clamp(req.SettleMs, 0, MaxSettleMs);
            if (settle > 0) await Task.Delay(settle, ct).ConfigureAwait(false);
            await WaitForFrameAsync(camera, ct).ConfigureAwait(false);

            var found = await DetectAsync(req, profile, settings, device, ct).ConfigureAwait(false);
            if (!found.IsSuccess) return found;
            var obj = found.Value;

            onResult(obj);
            if (!obj.Found)
            {
                return req.RequireMatch
                    ? Fail(ErrorCode.OperationFailed, req, "nothing detected.")
                    : Result<DetectedObject>.Ok(obj);
            }

            _log(LogLevel.Info,
                $"{req.Label}: {(obj.Manual ? "operator mark" : "detected")} X {obj.X:F4}  Y {obj.Y:F4}  P {obj.P:F2}°" +
                (obj.Manual ? string.Empty : $"  score {obj.Score:F2}"));

            if (req.MoveToResult)
            {
                var moved = await MoveToResultAsync(req, obj, device, ct).ConfigureAwait(false);
                if (!moved.IsSuccess) return Result<DetectedObject>.Fail(moved.Error, moved.Message);
            }
            return Result<DetectedObject>.Ok(obj);
        }
        finally
        {
            if (lease.IsSuccess)
                await _engine.Camera.StopGrabbingAsync(camera).ConfigureAwait(false);
        }
    }

    /// <summary>Detection, then the operator mark when it found nothing and the block asks for one.</summary>
    private async Task<Result<DetectedObject>> DetectAsync(
        DetectObjectRequest req, VisionProfile profile, AppSettings settings, string device, CancellationToken ct)
    {
        string camera = profile.CameraId;
        var pose = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!pose.IsSuccess) return Fail(pose.Error, req, $"read {device} — {pose.Message}");

        var det = await _detect(profile).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var state = _engine.Camera.GetState(camera);
        if (state.Width <= 0 || state.Height <= 0)
            return Fail(ErrorCode.OperationFailed, req, $"camera '{camera}' frame size unknown — is it grabbing?");

        var obj = DetectedObject.NotFound;
        if (det.IsSuccess)
            obj = DetectObjectMath.FromResult(profile, det.Value, pose.Value, state.Width, state.Height, settings);
        if (obj.Found)
            return Result<DetectedObject>.Ok(DetectObjectMath.Select(obj, pose.Value, req.UseX, req.UseY, req.UseP));

        _log(LogLevel.Warning, $"{req.Label}: {(det.IsSuccess ? "no detection" : det.Message)}");
        if (!req.AskOperator) return Result<DetectedObject>.Ok(obj);

        var prompt = _prompt();
        if (prompt == null)
        {
            _log(LogLevel.Warning, $"{req.Label}: 'ask operator' is on but no operator UI is attached.");
            return Result<DetectedObject>.Ok(obj);
        }

        var mark = await prompt.MarkLocationAsync(
            new MarkLocationRequest(camera, req.Label,
                "Detection failed. Click the required location on the image, then Confirm."), ct).ConfigureAwait(false);
        if (mark == null || mark.SrcW <= 0 || mark.SrcH <= 0 || !IsFinite(mark.Px, mark.Py))
        {
            _log(LogLevel.Warning, $"{req.Label}: operator did not mark a location.");
            return Result<DetectedObject>.Ok(obj);
        }

        // The operator may have jogged while the window was open — use the pose as it is now.
        var now = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!now.IsSuccess) return Fail(now.Error, req, $"read {device} — {now.Message}");
        return Result<DetectedObject>.Ok(DetectObjectMath.Select(
            DetectObjectMath.FromMark(mark, now.Value, settings), now.Value, req.UseX, req.UseY, req.UseP));
    }

    /// <summary>The guarded move on its own (the Vision helper's "Test move"), on the camera device from app settings.</summary>
    public Task<Result> MoveToResultAsync(DetectObjectRequest req, DetectedObject obj, CancellationToken ct)
    {
        var s = _settings();
        return MoveToResultAsync(req, obj, string.IsNullOrWhiteSpace(s.MoveClickDevice) ? "gantry-main" : s.MoveClickDevice, ct);
    }

    /// <summary>Centre the camera on the result (guarded), then the optional offset move.</summary>
    private async Task<Result> MoveToResultAsync(DetectObjectRequest req, DetectedObject obj, string device, CancellationToken ct)
    {
        var here = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!here.IsSuccess) return Result.Fail(here.Error, $"{req.Label}: read {device} — {here.Message}");

        double dx = obj.X - here.Value.X, dy = obj.Y - here.Value.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double guard = req.Guard;
        if (!double.IsFinite(dist) || dist > guard)
            return Result.Fail(ErrorCode.InvalidParameter,
                $"{req.Label}: move to result needs {dist:F3} mm, over the {guard:F2} mm guard — refused.");

        var target = here.Value with { X = obj.X, Y = obj.Y };
        _log(LogLevel.Info, $"{req.Label}: {device} → result, Δ ({dx:+0.000;-0.000}, {dy:+0.000;-0.000}) mm.");
        var moved = await MoveAndConfirmAsync(device, target, "result", req, ct).ConfigureAwait(false);
        if (!moved.IsSuccess || !req.HasOffset) return moved;

        var offset = target with { X = target.X + req.OffsetX, Y = target.Y + req.OffsetY, Z = target.Z + req.OffsetZ };
        _log(LogLevel.Info,
            $"{req.Label}: {device} offset ({req.OffsetX:+0.000;-0.000}, {req.OffsetY:+0.000;-0.000}, {req.OffsetZ:+0.000;-0.000}) mm.");
        return await MoveAndConfirmAsync(device, offset, "offset point", req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One move, then a check the device really stands on the target — a STOP mid-move also ends
    /// the move "successfully", and the next stage must never start from somewhere else.
    /// </summary>
    private async Task<Result> MoveAndConfirmAsync(
        string device, MotionPosition target, string what, DetectObjectRequest req, CancellationToken ct)
    {
        var moved = await _engine.Motion.MoveToPositionAsync(device, target, ct).ConfigureAwait(false);
        if (!moved.IsSuccess) return Result.Fail(moved.Error, $"{req.Label}: move to {what} — {moved.Message}");
        ct.ThrowIfCancellationRequested();

        var at = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!at.IsSuccess) return Result.Fail(at.Error, $"{req.Label}: confirm {what} — {at.Message}");
        bool near = Math.Abs(at.Value.X - target.X) <= ArrivalToleranceMm
                 && Math.Abs(at.Value.Y - target.Y) <= ArrivalToleranceMm
                 && Math.Abs(at.Value.Z - target.Z) <= ArrivalToleranceMm;
        return near
            ? Result.Ok()
            : Result.Fail(ErrorCode.MotionAxisError, $"{req.Label}: {device} stopped short of the {what}.");
    }

    /// <summary>Waits (bounded) for two frames newer than now, so detection sees the settled scene.</summary>
    private async Task WaitForFrameAsync(string cameraId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int seen = 0;
        void OnFrame(CameraFrame f)
        {
            if (!string.Equals(f.Camera, cameraId, StringComparison.OrdinalIgnoreCase)) return;
            if (Interlocked.Increment(ref seen) >= 2) tcs.TrySetResult(true);
        }

        _engine.Camera.FrameReady += OnFrame;
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(FrameWaitMs, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            _engine.Camera.FrameReady -= OnFrame;
        }
    }

    private static bool IsFinite(params double[] values) => values.All(double.IsFinite);

    private static Result<DetectedObject> Fail(ErrorCode code, DetectObjectRequest req, string message)
        => Result<DetectedObject>.Fail(code, $"{req.Label}: {message}");
}
