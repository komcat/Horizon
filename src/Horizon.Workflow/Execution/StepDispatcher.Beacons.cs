using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow;

/// <summary>
/// <c>beacon.plant</c> / <c>beacon.remove</c> — virtual markers on the live views (see
/// <c>docs/beacon-design.md</c>). A beacon is a point in the camera device's axis values, kept in
/// the in-memory <see cref="BeaconRegistry"/>; it outlives the program that planted it.
/// </summary>
internal sealed partial class StepDispatcher
{
    private async Task<Result> PlantBeaconAsync(WorkflowStep step, WorkflowContext ctx)
    {
        bool show = !Has(step, "show") || Bool(step, "show", ctx);
        var r = await _beacons.PlantHereAsync(
            _engine, new AppSettingsStore().Current, Str(step, "name", ctx), show, _log).ConfigureAwait(false);
        return r.IsSuccess ? Result.Ok() : Result.Fail(r.Error, r.Message);
    }

    /// <summary>
    /// <c>object.register</c> — ties a gripped object's marker to its tool device. The point comes
    /// from a Detect Object result earlier in the program (<c>{from}_found / _x / _y</c>); from now
    /// on the marker moves with the tool. A result that found nothing fails the step: there is
    /// nothing to register.
    /// </summary>
    private async Task<Result> RegisterGrippedObjectAsync(WorkflowStep step, WorkflowContext ctx)
    {
        var from = Has(step, "from") ? Str(step, "from", ctx).Trim() : "obj";
        var tool = Str(step, "tool", ctx).Trim();
        if (tool.Length == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "object.register: no tool device.");

        // 'at' = camera: plant where the camera is now (the crosshair) — no Detect Object needed.
        // 'at' = detect (also a block saved before this option existed): use a Detect Object result.
        double? x = null, y = null;
        bool atCamera = Str(step, "at", ctx).Trim().Equals("camera", StringComparison.OrdinalIgnoreCase);
        if (!atCamera)
        {
            if (ctx.GetString($"{from}_found").Trim() != "1")
                return Result.Fail(ErrorCode.OperationFailed,
                    $"object.register: no detection to register — '{from}_found' is not 1. Run a Detect Object block with capture prefix '{from}' first, or set 'Plant at' to 'camera'.");

            var xText = ctx.GetString($"{from}_x");
            var yText = ctx.GetString($"{from}_y");
            if (!double.TryParse(xText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dx) ||
                !double.TryParse(yText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dy))
                return Result.Fail(ErrorCode.InvalidParameter, $"object.register: '{from}_x' / '{from}_y' are not numbers ('{xText}', '{yText}').");
            x = dx;
            y = dy;
        }

        bool show = !Has(step, "show") || Bool(step, "show", ctx);

        // Optional outline snapshot: a box around the crosshair, filtered to black / white.
        GrippedOutlineOptions? outline = null;
        if (Bool(step, "outline", ctx))
        {
            int w = Has(step, "roiWidth") ? (int)Num(step, "roiWidth", ctx) : GrippedOutlineOptions.DefaultSize;
            int h = Has(step, "roiHeight") ? (int)Num(step, "roiHeight", ctx) : GrippedOutlineOptions.DefaultSize;
            outline = new GrippedOutlineOptions(w, h,
                GrippedOutlineOptions.ParseFilters(step.Parameters.GetValueOrDefault("filters", string.Empty)));
        }

        var r = await _registerGrippedMarker(Str(step, "name", ctx), tool, x, y, show, outline).ConfigureAwait(false);
        return r.IsSuccess ? Result.Ok() : Result.Fail(r.Error, r.Message);
    }

    /// <summary>
    /// <c>beacon.line</c> — a construction line from one beacon to another. Measures it now
    /// (<c>{captureAs}_length / _angle / _cx / _cy / _x1 / _y1 / _x2 / _y2</c>, camera device mm;
    /// angle in the Detect Object P convention) and keeps it for the live views to draw.
    /// </summary>
    private Result DrawBeaconLine(WorkflowStep step, WorkflowContext ctx)
    {
        var from = Str(step, "from", ctx).Trim();
        var to = Str(step, "to", ctx).Trim();
        if (from.Length == 0 || to.Length == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "beacon.line: give both beacon names (from and to).");
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return Result.Fail(ErrorCode.InvalidParameter, $"beacon.line: 'from' and 'to' are the same beacon ('{from}').");

        if (_beacons.Find(from) is not { } a)
            return Result.Fail(ErrorCode.FileNotFound, $"beacon.line: there is no beacon named '{from}'.");
        if (_beacons.Find(to) is not { } b)
            return Result.Fail(ErrorCode.FileNotFound, $"beacon.line: there is no beacon named '{to}'.");
        if (!string.Equals(a.Device, b.Device, StringComparison.OrdinalIgnoreCase))
            return Result.Fail(ErrorCode.InvalidParameter,
                $"beacon.line: '{a.Name}' is on {a.Device} and '{b.Name}' on {b.Device} — a line needs both on the same camera device.");
        if (_beacons.Locate(a) is not { } pa)
            return Result.Fail(ErrorCode.OperationFailed, $"beacon.line: beacon '{a.Name}' cannot be placed right now (its tool is not registered or has no position).");
        if (_beacons.Locate(b) is not { } pb)
            return Result.Fail(ErrorCode.OperationFailed, $"beacon.line: beacon '{b.Name}' cannot be placed right now (its tool is not registered or has no position).");

        var m = BeaconLine.Measure(pa.X, pa.Y, pb.X, pb.Y, new AppSettingsStore().Current);
        if (!double.IsFinite(m.Length) || m.Length < 1e-6)
            return Result.Fail(ErrorCode.InvalidParameter, $"beacon.line: '{a.Name}' and '{b.Name}' are on the same point — there is no line to draw.");

        var prefix = Has(step, "captureAs") ? Str(step, "captureAs", ctx).Trim() : "line";
        if (prefix.Length == 0) prefix = "line";
        ctx.Set($"{prefix}_length", m.Length);
        ctx.Set($"{prefix}_angle", m.AngleDeg);
        ctx.Set($"{prefix}_cx", m.Cx);
        ctx.Set($"{prefix}_cy", m.Cy);
        ctx.Set($"{prefix}_x1", pa.X);
        ctx.Set($"{prefix}_y1", pa.Y);
        ctx.Set($"{prefix}_x2", pb.X);
        ctx.Set($"{prefix}_y2", pb.Y);

        var name = Str(step, "name", ctx).Trim();
        if (name.Length == 0) name = $"{a.Name}-{b.Name}";
        bool show = !Has(step, "show") || Bool(step, "show", ctx);
        _beacons.AddLine(name, a.Name, b.Name, show);
        _log(LogLevel.Info,
            $"Beacon line '{name}' ({a.Name} → {b.Name}): length {m.Length:F4} mm  angle {m.AngleDeg:F2}°  centre X {m.Cx:F4}  Y {m.Cy:F4} -> {prefix}_*");
        return Result.Ok();
    }

    /// <summary>The largest camera-to-beacon move the block will ever make, whatever it asks for (mm).</summary>
    private const double BeaconMoveHardMaxMm = 50.0;
    private const double BeaconArrivalToleranceMm = 0.01;

    /// <summary>
    /// <c>beacon.moveCamera</c> — centres the camera on a beacon: moves the beacon's camera device
    /// in X / Y only (Z and the other axes untouched). A move over <c>maxMoveMm</c> is
    /// <b>refused, never clamped</b>; afterwards the device must really stand on the point.
    /// </summary>
    private async Task<Result> MoveCameraToBeaconAsync(WorkflowStep step, WorkflowContext ctx, CancellationToken ct)
    {
        var name = Str(step, "name", ctx).Trim();
        if (name.Length == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "beacon.moveCamera: no beacon name.");
        if (_beacons.Find(name) is not { } beacon)
            return Result.Fail(ErrorCode.FileNotFound, $"beacon.moveCamera: there is no beacon named '{name}'.");
        if (_beacons.Locate(beacon) is not { } spot || !double.IsFinite(spot.X) || !double.IsFinite(spot.Y))
            return Result.Fail(ErrorCode.OperationFailed, $"beacon.moveCamera: beacon '{beacon.Name}' cannot be placed right now.");

        // Strict number: a typo must fail the step, never silently become a different guard.
        double guard = 10.0;
        if (Has(step, "maxMoveMm"))
        {
            var text = Str(step, "maxMoveMm", ctx);
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out guard)
                || !double.IsFinite(guard) || guard <= 0)
                return Result.Fail(ErrorCode.InvalidParameter, $"beacon.moveCamera: 'maxMoveMm' must be a number above 0 ('{text}').");
        }
        guard = Math.Min(guard, BeaconMoveHardMaxMm);

        string device = beacon.Device;
        var here = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!here.IsSuccess)
            return Result.Fail(here.Error, $"beacon.moveCamera: read {device} — {here.Message}");

        double dx = spot.X - here.Value.X, dy = spot.Y - here.Value.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(dist) || dist > guard)
            return Result.Fail(ErrorCode.InvalidParameter,
                $"beacon.moveCamera: '{beacon.Name}' is {dist:F3} mm away, over the {guard:F2} mm guard — refused.");

        var target = here.Value with { X = spot.X, Y = spot.Y };
        _log(LogLevel.Info, $"Camera to beacon '{beacon.Name}': {device} Δ ({dx:+0.000;-0.000}, {dy:+0.000;-0.000}) mm.");
        var moved = await _engine.Motion.MoveToPositionAsync(device, target, ct).ConfigureAwait(false);
        if (!moved.IsSuccess)
            return Result.Fail(moved.Error, $"beacon.moveCamera: move — {moved.Message}");
        ct.ThrowIfCancellationRequested();

        // A STOP mid-move also ends the move "successfully" — the next block must not start from somewhere else.
        var at = await _engine.Motion.GetPositionAsync(device).ConfigureAwait(false);
        if (!at.IsSuccess)
            return Result.Fail(at.Error, $"beacon.moveCamera: confirm — {at.Message}");
        bool near = Math.Abs(at.Value.X - target.X) <= BeaconArrivalToleranceMm
                 && Math.Abs(at.Value.Y - target.Y) <= BeaconArrivalToleranceMm;
        return near
            ? Result.Ok()
            : Result.Fail(ErrorCode.MotionAxisError, $"beacon.moveCamera: {device} stopped short of beacon '{beacon.Name}'.");
    }

    private Result RemoveBeacon(WorkflowStep step, WorkflowContext ctx)
    {
        bool byName = Str(step, "which", ctx).Trim().Equals("name", StringComparison.OrdinalIgnoreCase);
        if (!byName)
        {
            int n = _beacons.Clear();
            _log(LogLevel.Info, n > 0 ? $"All beacons removed ({n})." : "Remove beacons: there were none.");
            return Result.Ok();
        }

        var name = Str(step, "name", ctx).Trim();
        if (name.Length == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "beacon.remove: 'Remove' is set to 'name' but no beacon name is given.");
        // A beacon that is already gone (expired, replaced) is not a reason to stop a program.
        if (_beacons.Remove(name)) _log(LogLevel.Info, $"Beacon '{name}' removed.");
        else _log(LogLevel.Warning, $"Remove beacon: there is no beacon named '{name}'.");
        return Result.Ok();
    }
}
