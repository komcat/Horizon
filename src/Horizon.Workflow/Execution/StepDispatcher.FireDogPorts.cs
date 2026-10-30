using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.ClimbScan;
using Horizon.Workflow.Scanning;
using Horizon.Workflow.Scanning.Feedback;
using Horizon.Workflow.Xcan;

namespace Horizon.Workflow;

/// <summary>
/// Steps ported from FireDog for DLAM (see <c>docs/dlam-ui.md</c>): the XCAN / two-lens XCAN /
/// climb scans, <c>motion.liftOff</c>, <c>motion.movePath</c>, <c>io.waitInput</c> and
/// <c>flow.waitUntil</c>. FireDog's <c>manual_adjust</c> is the existing <c>flow.jogPopup</c>.
/// </summary>
internal sealed partial class StepDispatcher
{
    private const int FirstSampleTimeoutMs = 10_000;

    // --- feedback ------------------------------------------------------------------------

    /// <summary>
    /// Builds the step's feedback source — the catalog <c>signal</c> key when set, else the
    /// <c>feedbackKind/feedbackDevice/feedbackChannel/feedbackValue</c> descriptor (an SMU
    /// descriptor STREAMS, which the fresh-sample gate counts) — starts it, waits for its first
    /// sample, runs <paramref name="body"/>, and always stops it.
    /// </summary>
    private async Task<Result> WithFeedbackAsync(
        WorkflowStep step, WorkflowContext ctx, Func<IScanFeedbackSource, CancellationToken, Task<Result>> body, CancellationToken ct)
    {
        IScanFeedbackSource source;
        if (Has(step, "signal"))
        {
            var key = Str(step, "signal", ctx);
            var d = _signals.Describe().FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            source = new SignalScanFeedbackSource((k, t) => _signals.ReadAsync(k, t), key, d?.Display ?? key, d?.Unit ?? string.Empty);
        }
        else if (Has(step, "feedbackDevice"))
        {
            source = ScanFeedbackFactory.Create(_engine, new FeedbackDescriptor
            {
                Kind = ParseFeedbackKind(Str(step, "feedbackKind", ctx)),
                Device = Str(step, "feedbackDevice", ctx),
                Channel = Has(step, "feedbackChannel") ? (int)Num(step, "feedbackChannel", ctx) : 1,
                ValueType = Str(step, "feedbackValue", ctx).Trim().Equals("voltage", StringComparison.OrdinalIgnoreCase)
                    ? FeedbackValue.Voltage : FeedbackValue.Current,
                IntervalMs = Has(step, "feedbackIntervalMs") ? (int)Num(step, "feedbackIntervalMs", ctx) : 20,
            });
        }
        else
        {
            return Result.Fail(ErrorCode.InvalidParameter, $"{step.Op}: no feedback — set 'signal' (or 'feedbackDevice').");
        }

        try
        {
            var started = await source.StartAsync(ct).ConfigureAwait(false);
            if (!started.IsSuccess)
                return Result.Fail(started.Error, $"{step.Op}: feedback failed to start — {started.Message}");
            await FreshSampler.WaitForFirstSampleAsync(source, FirstSampleTimeoutMs, ct).ConfigureAwait(false);
            _log(LogLevel.Info, $"{step.Op} feedback: {source.Description}.");
            return await body(source, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            return Result.Fail(ErrorCode.Timeout, $"{step.Op}: {ex.Message}");
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Multiplier from the step's signal-level params to the signal's own unit. <c>unit</c> = uA |
    /// nA | mA | raw; blank = uA for a current signal (FireDog's convention), raw otherwise.
    /// </summary>
    private static Result<double> LevelScale(WorkflowStep step, WorkflowContext ctx, IScanFeedbackSource fb)
    {
        var unit = Str(step, "unit", ctx).Trim().ToLowerInvariant();
        if (unit.Length == 0) unit = fb.Unit == "A" ? "ua" : "raw";
        return unit switch
        {
            "ua" or "µa" => Result<double>.Ok(1e-6),
            "na" => Result<double>.Ok(1e-9),
            "ma" => Result<double>.Ok(1e-3),
            "raw" or "a" or "v" => Result<double>.Ok(1.0),
            _ => Result<double>.Fail(ErrorCode.InvalidParameter, $"{step.Op}: unit '{unit}' must be uA, nA, mA or raw."),
        };
    }

    // --- XCAN ----------------------------------------------------------------------------

    /// <summary>The XCAN axis order is always Z, Y, X (then U, V, W): the gap axis first. A step chooses the SET.</summary>
    private static readonly string[] XcanAxisOrder = { "Z", "Y", "X", "U", "V", "W" };

    private static XcanParameters ReadXcanPass(WorkflowStep step, WorkflowContext ctx, double scale)
    {
        var p = new XcanParameters();
        if (Has(step, "axes"))
        {
            var set = SplitAxes(Str(step, "axes", ctx));
            var ordered = XcanAxisOrder.Where(a => set.Contains(a)).ToArray();
            p = p with { Axes = ordered.Length == set.Distinct().Count() ? ordered : set };   // an unknown axis is kept so Validate names it
        }
        if (Has(step, "stepsUm")) p = p with { StepsMm = ParseStepSizesMm(Str(step, "stepsUm", ctx)) };
        return p with
        {
            FreshSamples = (int)NumOr(step, ctx, "freshSamples", p.FreshSamples),
            SettleCeilingMs = (int)NumOr(step, ctx, "settleMs", p.SettleCeilingMs),
            SettleMinMs = (int)NumOr(step, ctx, "settleMinMs", p.SettleMinMs),
            AvgCount = (int)NumOr(step, ctx, "avgCount", p.AvgCount),
            ConsecLimit = (int)NumOr(step, ctx, "consecLimit", p.ConsecLimit),
            SigDropPct = NumOr(step, ctx, "sigDropPct", p.SigDropPct),
            MaxTravelMm = NumOr(step, ctx, "maxTravelUm", p.MaxTravelMm * 1000) / 1000.0,
            ImprovePct = NumOr(step, ctx, "improvePct", p.ImprovePct),
            MinSignal = NumOr(step, ctx, "minSignal", 0) * scale,
            ExitThreshold = NumOr(step, ctx, "exitTarget", 0) * scale,
            VelocityMmPerSec = NumOr(step, ctx, "velocity", p.VelocityMmPerSec),
            Adaptive = BoolOr(step, ctx, "adaptive", p.Adaptive),
            ZoneLow = Has(step, "zoneLow") ? Num(step, "zoneLow", ctx) * scale : p.ZoneLow,
            ZoneHigh = Has(step, "zoneHigh") ? Num(step, "zoneHigh", ctx) * scale : p.ZoneHigh,
            SkipOpposite = BoolOr(step, ctx, "skipOpposite", p.SkipOpposite),
            DirMemory = BoolOr(step, ctx, "dirMemory", p.DirMemory),
            ReuseSample = BoolOr(step, ctx, "reuseSample", p.ReuseSample),
            Trace = BoolOr(step, ctx, "trace", p.Trace),
            MoveTimeoutMs = (int)NumOr(step, ctx, "moveTimeoutMs", p.MoveTimeoutMs),
        };
    }

    /// <summary>
    /// <c>scan.xcan</c> — one XCAN pass on <c>device</c>. Results land in <c>{captureAs}_*</c>
    /// (default <c>xcan</c>): baseline / best / peak / delta / gain / exitHit / passes, in the
    /// signal's own unit. A pass that ran is a success; a motion / setup failure fails the step.
    /// </summary>
    private async Task<Result> RunXcanAsync(WorkflowStep step, WorkflowContext ctx, IScanFeedbackSource fb, CancellationToken ct)
    {
        var scale = LevelScale(step, ctx, fb);
        if (!scale.IsSuccess) return Result.Fail(scale.Error, scale.Message);

        var r = await new XcanEngine(_engine, _log)
            .RunAsync(step.Device, fb, ReadXcanPass(step, ctx, scale.Value), ct).ConfigureAwait(false);

        var prefix = CaptureAs(step, ctx, "xcan");
        ctx.Set($"{prefix}_baseline", r.Baseline);
        ctx.Set($"{prefix}_best", r.Best);
        ctx.Set($"{prefix}_peak", r.Peak);
        ctx.Set($"{prefix}_delta", r.Delta);
        ctx.Set($"{prefix}_gain", r.Gain);
        ctx.Set($"{prefix}_exitHit", r.ExitHit ? "1" : "0");
        ctx.Set($"{prefix}_passes", r.PassesRun);
        return r.Success ? Result.Ok(r.Message) : Result.Fail(ErrorCode.OperationFailed, r.Message);
    }

    /// <summary>
    /// <c>scan.xcanTwoLens</c> — left coarse, left coarse again, left fine, right coarse, ending the
    /// moment the target is reached. Results in <c>{captureAs}_*</c> (default <c>xtl</c>): reached /
    /// passes / last / best / live. Missing the target fails the step only with <c>failIfMissed</c>.
    /// </summary>
    private async Task<Result> RunXcanTwoLensAsync(WorkflowStep step, WorkflowContext ctx, IScanFeedbackSource fb, CancellationToken ct)
    {
        var scale = LevelScale(step, ctx, fb);
        if (!scale.IsSuccess) return Result.Fail(scale.Error, scale.Message);
        double k = scale.Value;

        var defaults = new XcanTwoLensParameters();
        var p = new XcanTwoLensParameters
        {
            LeftDevice = Has(step, "left") ? Str(step, "left", ctx) : defaults.LeftDevice,
            RightDevice = Has(step, "right") ? Str(step, "right", ctx) : defaults.RightDevice,
            Target = Num(step, "target", ctx) * k,
            ZoneLow = NumOr(step, ctx, "zoneLow", 200) * k,
            ZoneHigh = NumOr(step, ctx, "zoneHigh", 0) * k,
            CoarseStepsMm = Has(step, "coarseStepsUm") ? ParseStepSizesMm(Str(step, "coarseStepsUm", ctx)) : defaults.CoarseStepsMm,
            FineStepsMm = Has(step, "fineStepsUm") ? ParseStepSizesMm(Str(step, "fineStepsUm", ctx)) : defaults.FineStepsMm,
            Pass = ReadXcanPass(step, ctx, k),
        };

        var r = await new XcanTwoLens(_engine, _log).RunAsync(fb, p, ct).ConfigureAwait(false);

        var prefix = CaptureAs(step, ctx, "xtl");
        ctx.Set($"{prefix}_reached", r.Reached ? "1" : "0");
        ctx.Set($"{prefix}_passes", r.Passes);
        ctx.Set($"{prefix}_last", r.Last);
        ctx.Set($"{prefix}_best", r.Best);
        ctx.Set($"{prefix}_live", r.Live);
        _log(r.Success && r.Reached ? LogLevel.Info : LogLevel.Warning, r.Message);
        if (!r.Success) return Result.Fail(ErrorCode.OperationFailed, r.Message);
        if (!r.Reached && Bool(step, "failIfMissed", ctx)) return Result.Fail(ErrorCode.OperationFailed, r.Message);
        return Result.Ok(r.Message);
    }

    // --- climb ---------------------------------------------------------------------------

    /// <summary>
    /// <c>scan.climb</c> — the gap-closing Z + XY plane search. Results in <c>{captureAs}_*</c>
    /// (default <c>climb</c>). The stage always ends on the run's best point.
    /// </summary>
    private async Task<Result> RunClimbAsync(WorkflowStep step, WorkflowContext ctx, IScanFeedbackSource fb, CancellationToken ct)
    {
        var scale = LevelScale(step, ctx, fb);
        if (!scale.IsSuccess) return Result.Fail(scale.Error, scale.Message);

        var d = new ClimbScanParameters();
        string a = d.PlaneAxisA, b = d.PlaneAxisB;
        if (Has(step, "planeAxes"))
        {
            var plane = SplitAxes(Str(step, "planeAxes", ctx));
            if (plane.Length != 2)
                return Result.Fail(ErrorCode.InvalidParameter, "scan.climb: 'planeAxes' needs exactly two axes, e.g. \"X,Y\".");
            (a, b) = (plane[0], plane[1]);
        }
        var scanText = Str(step, "planeScan", ctx).Trim();
        var p = d with
        {
            ZAxis = Has(step, "zAxis") ? Str(step, "zAxis", ctx).Trim() : d.ZAxis,
            PlaneAxisA = a,
            PlaneAxisB = b,
            ZDir = NumOr(step, ctx, "zDir", d.ZDir) < 0 ? -1 : 1,
            ZStepMm = NumOr(step, ctx, "zStepUm", d.ZStepMm * 1000) / 1000.0,
            ZSettleMs = (int)NumOr(step, ctx, "zSettleMs", d.ZSettleMs),
            XyStepMm = NumOr(step, ctx, "xyStepUm", d.XyStepMm * 1000) / 1000.0,
            XySteps = (int)NumOr(step, ctx, "xySteps", d.XySteps),
            PlaneScan = scanText.Equals("spiral", StringComparison.OrdinalIgnoreCase) ? ClimbPlaneScan.Spiral : ClimbPlaneScan.Cross,
            PeakDrop = (int)NumOr(step, ctx, "peakDrop", d.PeakDrop),
            CrossPasses = (int)NumOr(step, ctx, "crossPasses", d.CrossPasses),
            RingsStop = (int)NumOr(step, ctx, "ringsStop", d.RingsStop),
            ImprovePct = NumOr(step, ctx, "improvePct", d.ImprovePct),
            ZProbePct = NumOr(step, ctx, "zProbePct", d.ZProbePct),
            MaxSkips = (int)NumOr(step, ctx, "maxSkips", d.MaxSkips),
            Target = Num(step, "target", ctx) * scale.Value,
            TargetPct = NumOr(step, ctx, "targetPct", d.TargetPct),
            MaxZMm = NumOr(step, ctx, "maxZUm", d.MaxZMm * 1000) / 1000.0,
            MaxZSteps = (int)NumOr(step, ctx, "maxZSteps", d.MaxZSteps),
            ContactDropPct = NumOr(step, ctx, "contactDropPct", d.ContactDropPct),
            StallPlanes = (int)NumOr(step, ctx, "stallPlanes", d.StallPlanes),
            RasterStart = BoolOr(step, ctx, "rasterStart", d.RasterStart),
            FreshSamples = (int)NumOr(step, ctx, "freshSamples", d.FreshSamples),
            SettleCeilingMs = (int)NumOr(step, ctx, "settleMs", d.SettleCeilingMs),
            SettleMinMs = (int)NumOr(step, ctx, "settleMinMs", d.SettleMinMs),
            AvgCount = (int)NumOr(step, ctx, "avgCount", d.AvgCount),
            VelocityMmPerSec = NumOr(step, ctx, "velocity", d.VelocityMmPerSec),
            Trace = BoolOr(step, ctx, "trace", d.Trace),
            MoveTimeoutMs = (int)NumOr(step, ctx, "moveTimeoutMs", d.MoveTimeoutMs),
        };
        if (scanText.Length > 0 && p.PlaneScan == ClimbPlaneScan.Cross && !scanText.Equals("cross", StringComparison.OrdinalIgnoreCase))
            return Result.Fail(ErrorCode.InvalidParameter, $"scan.climb: planeScan '{scanText}' must be cross or spiral.");

        var r = await new ClimbScanEngine(_engine, _log).RunAsync(step.Device, fb, p, ct).ConfigureAwait(false);

        var prefix = CaptureAs(step, ctx, "climb");
        ctx.Set($"{prefix}_baseline", r.Baseline);
        ctx.Set($"{prefix}_best", r.Best);
        ctx.Set($"{prefix}_final", r.Final);
        ctx.Set($"{prefix}_target", r.Target);
        ctx.Set($"{prefix}_targetFrac", r.TargetFraction);
        ctx.Set($"{prefix}_stop", r.Stop.ToString());
        ctx.Set($"{prefix}_exitHit", r.ExitHit ? "1" : "0");
        ctx.Set($"{prefix}_zUm", r.ZTravelUm);
        ctx.Set($"{prefix}_bestZUm", r.BestZUm);
        ctx.Set($"{prefix}_bestAUm", r.BestAUm);
        ctx.Set($"{prefix}_bestBUm", r.BestBUm);
        ctx.Set($"{prefix}_planes", r.PlanesSearched);
        ctx.Set($"{prefix}_skipped", r.PlanesSkipped);
        ctx.Set($"{prefix}_points", r.Points);
        ctx.Set($"{prefix}_gain", r.Gain);
        return r.Success ? Result.Ok(r.Message) : Result.Fail(ErrorCode.OperationFailed, r.Message);
    }

    // --- motion --------------------------------------------------------------------------

    /// <summary>Lift-off cap (mm) — FireDog's.</summary>
    private const double MaxLiftMm = 50.0;

    /// <summary>
    /// <c>motion.liftOff</c> — lift along one signed local axis (<c>liftAxis</c> "X+" / "Z-"; blank =
    /// by controller: PI hexapod X+, ACS gantry Z−) by <c>liftMm</c> (default 2; 0 = no lift), THEN
    /// move to the named <c>position</c> with the step's move overrides (velocity / camera / exposure /
    /// approach). The lift runs at the device's current velocity. Everything is checked before anything moves.
    /// </summary>
    private async Task<Result> LiftOffAsync(WorkflowStep step, WorkflowContext ctx, CancellationToken ct)
    {
        var info = _engine.Motion.GetDevice(step.Device);
        if (info == null)
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"motion.liftOff: device '{step.Device}' not found.");
        var position = Str(step, "position", ctx).Trim();
        if (position.Length == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "motion.liftOff: no position.");
        if (_getPosition(step.Device, position) == null)
            return Result.Fail(ErrorCode.FileNotFound, $"motion.liftOff: position '{position}' not found on {step.Device}.");

        double lift = NumOr(step, ctx, "liftMm", 2.0);
        if (!(lift >= 0 && lift <= MaxLiftMm))
            return Result.Fail(ErrorCode.InvalidParameter, $"motion.liftOff: liftMm {lift} must be 0..{MaxLiftMm:0}.");

        var axisText = Str(step, "liftAxis", ctx).Trim().ToUpperInvariant();
        if (axisText.Length == 0)
        {
            axisText = info.ControllerType.Trim().ToUpperInvariant() switch
            {
                "PI" => "X+",
                "ACS" => "Z-",
                _ => string.Empty,
            };
            if (axisText.Length == 0)
                return Result.Fail(ErrorCode.InvalidParameter,
                    $"motion.liftOff: no default lift axis for a {info.ControllerType} controller — set liftAxis (e.g. \"Z-\").");
        }
        if (axisText.Length != 2 || axisText[1] is not ('+' or '-'))
            return Result.Fail(ErrorCode.InvalidParameter, $"motion.liftOff: liftAxis '{axisText}' must be an axis and a sign, e.g. \"X+\" or \"Z-\".");
        var axis = axisText[..1];
        if (!info.Axes.Any(x => string.Equals(x.Trim(), axis, StringComparison.OrdinalIgnoreCase)))
            return Result.Fail(ErrorCode.InvalidParameter, $"motion.liftOff: {step.Device} has no axis {axis}.");

        if (lift > 0)
        {
            double delta = axisText[1] == '+' ? lift : -lift;
            _log(LogLevel.Info, $"{step.Device}: lift off {lift:0.###} mm along {axisText}");
            var up = await _engine.Motion.MoveRelativeAsync(step.Device, axis, delta, ct).ConfigureAwait(false);
            if (!up.IsSuccess) return Result.Fail(up.Error, $"motion.liftOff: lift failed — {up.Message}");
        }

        if (!TryMoveLighting(step, ctx, out var lighting, out var bad)) return bad;
        return await _moveToNamed(step.Device, position, MoveOverridesOf(step, ctx) with { Lighting = lighting }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>motion.movePath</c> — moves along the device's position map (Machine › Motion) to <c>to</c>,
    /// leg by leg over the fewest-hop linked route, confirming arrival after each leg. The device
    /// must already stand on a node: <c>from</c> names it (and is checked), or blank = whichever node
    /// it stands on, within <c>startTolMm</c> (default 0.01). No implicit move to the start. Velocity /
    /// camera / exposure apply to every leg, an approach only to the last.
    /// </summary>
    private async Task<Result> MovePathAsync(WorkflowStep step, WorkflowContext ctx, CancellationToken ct)
    {
        var info = _engine.Motion.GetDevice(step.Device);
        if (info == null)
            return Result.Fail(ErrorCode.MotionDeviceNotFound, $"motion.movePath: device '{step.Device}' not found.");
        var to = Str(step, "to", ctx).Trim();
        if (to.Length == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "motion.movePath: no destination ('to').");
        if (_getPosition(step.Device, to) == null)
            return Result.Fail(ErrorCode.FileNotFound, $"motion.movePath: position '{to}' not found on {step.Device}.");
        double tol = Math.Clamp(NumOr(step, ctx, "startTolMm", PositionMatch.Tolerance), 0.0001, 5.0);

        var map = _loadPositionMap(step.Device);
        if (map.Edges.Count == 0)
            return Result.Fail(ErrorCode.InvalidParameter,
                $"motion.movePath: {step.Device} has no linked position map in this product (Machine › Motion).");

        var live = await _engine.Motion.GetPositionAsync(step.Device).ConfigureAwait(false);
        if (!live.IsSuccess) return Result.Fail(live.Error, $"motion.movePath: read position — {live.Message}");

        string? start;
        var from = Str(step, "from", ctx).Trim();
        if (from.Length > 0)
        {
            var np = _getPosition(step.Device, from);
            if (np == null)
                return Result.Fail(ErrorCode.FileNotFound, $"motion.movePath: start '{from}' not found on {step.Device}.");
            if (!PositionMatch.Matches(np.Coordinates, live.Value, info.Axes, tol))
                return Result.Fail(ErrorCode.OperationFailed,
                    $"motion.movePath: {step.Device} is not at the start '{from}' (tolerance {tol} mm) — move there first.");
            start = from;
        }
        else
        {
            start = map.Nodes.Select(n => n.Name)
                .FirstOrDefault(n => PositionMatch.Matches(_getPosition(step.Device, n)?.Coordinates, live.Value, info.Axes, tol));
            if (start == null)
                return Result.Fail(ErrorCode.OperationFailed,
                    $"motion.movePath: {step.Device} is not on a map node (tolerance {tol} mm) — move it onto one first.");
        }

        var route = PositionRoute.Find(map, start, to, n => _getPosition(step.Device, n) != null);
        if (route == null)
            return Result.Fail(ErrorCode.OperationFailed, $"motion.movePath: no linked route from '{start}' to '{to}'.");
        if (route.Count == 1)
        {
            _log(LogLevel.Info, $"{step.Device} already at '{to}'.");
            return Result.Ok();
        }

        var overrides = MoveOverridesOf(step, ctx);
        var legOverrides = overrides with { ApproachDistance = 0, ApproachVelocity = 0 };
        for (int i = 1; i < route.Count; i++)
        {
            var stop = route[i];
            bool last = i == route.Count - 1;
            var r = await _moveToNamed(step.Device, stop, last ? overrides : legOverrides, ct).ConfigureAwait(false);
            if (!r.IsSuccess)
                return Result.Fail(r.Error, $"motion.movePath: leg {i}/{route.Count - 1} to '{stop}' — {r.Message}");

            // A STOP mid-move also ends a move "successfully" — never start the next leg off the node.
            var at = await _engine.Motion.GetPositionAsync(step.Device).ConfigureAwait(false);
            if (!at.IsSuccess || !PositionMatch.Matches(_getPosition(step.Device, stop)?.Coordinates, at.Value, info.Axes))
                return Result.Fail(ErrorCode.MotionAxisError,
                    $"motion.movePath: {step.Device} stopped short of '{stop}' (leg {i}/{route.Count - 1}).");
            _log(LogLevel.Info, $"{step.Device}: reached '{stop}' ({i}/{route.Count - 1})");
        }
        return Result.Ok();
    }

    // --- waits ---------------------------------------------------------------------------

    /// <summary>
    /// <c>io.waitInput</c> — waits for a digital input (<c>pin</c>: name or 0-based index) to read
    /// <c>state</c> (default on), polling the IO device's state every <c>pollMs</c> (default 20) for at
    /// most <c>timeoutMs</c> (default 10 000, max 600 000).
    /// </summary>
    private async Task<Result> WaitInputAsync(WorkflowStep step, WorkflowContext ctx, CancellationToken ct)
    {
        var info = _engine.IO.GetDevice(step.Device);
        if (info == null)
            return Result.Fail(ErrorCode.InvalidParameter, $"io.waitInput: IO device '{step.Device}' not found.");
        var pinText = Str(step, "pin", ctx).Trim();
        int pin = info.InputPins.FirstOrDefault(p => string.Equals(p.Name, pinText, StringComparison.OrdinalIgnoreCase))?.Pin
                  ?? (int.TryParse(pinText, out var n) ? n : -1);
        if (pin < 0 || pin >= Math.Min(info.InputCount, 32))
            return Result.Fail(ErrorCode.InvalidParameter, $"io.waitInput: '{pinText}' is not an input of {step.Device}.");

        bool want = !Has(step, "state") || Bool(step, "state", ctx);
        int timeoutMs = BoundedMs(step, ctx, "timeoutMs", 10_000, 10, 600_000);
        int pollMs = BoundedMs(step, ctx, "pollMs", 20, 1, 5_000);
        long deadline = Environment.TickCount64 + timeoutMs;
        int maxPolls = timeoutMs / pollMs + 2;
        for (int i = 0; i < maxPolls; i++)
        {
            var st = _engine.IO.GetState(step.Device);
            if (st.Valid && (((st.Inputs >> pin) & 1u) == 1u) == want)
            {
                _log(LogLevel.Info, $"io.waitInput: {step.Device}.{pinText} = {(want ? "ON" : "OFF")}");
                return Result.Ok();
            }
            if (Environment.TickCount64 >= deadline) break;
            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }
        return Result.Fail(ErrorCode.Timeout,
            $"io.waitInput: {step.Device}.{pinText} did not read {(want ? "ON" : "OFF")} within {timeoutMs} ms.");
    }

    /// <summary>
    /// <c>flow.waitUntil</c> — waits until <c>signal</c> (a catalog key, read live) or <c>var</c> (a
    /// context variable, e.g. set by a parallel lane) compares true with <c>op</c> (&gt; &lt; &gt;= &lt;= == !=)
    /// against <c>value</c>; polls every <c>pollMs</c> (default 50), fails after <c>timeoutMs</c>
    /// (default 10 000, max 3 600 000).
    /// </summary>
    private async Task<Result> WaitUntilAsync(WorkflowStep step, WorkflowContext ctx, CancellationToken ct)
    {
        var key = Str(step, "signal", ctx).Trim();
        var variable = Str(step, "var", ctx).Trim();
        if ((key.Length == 0) == (variable.Length == 0))
            return Result.Fail(ErrorCode.InvalidParameter, "flow.waitUntil: set exactly one of 'signal' or 'var'.");
        var op = Str(step, "op", ctx).Trim();
        double rhs = Num(step, "value", ctx);
        if (!TryCompare(0, op, rhs, out _))
            return Result.Fail(ErrorCode.InvalidParameter, $"flow.waitUntil: unknown comparison operator '{op}'.");

        int timeoutMs = BoundedMs(step, ctx, "timeoutMs", 10_000, 10, 3_600_000);
        int pollMs = BoundedMs(step, ctx, "pollMs", 50, 1, 5_000);
        string label = key.Length > 0 ? key : variable;
        long deadline = Environment.TickCount64 + timeoutMs;
        int maxPolls = timeoutMs / pollMs + 2;
        double lhs = double.NaN;
        for (int i = 0; i < maxPolls; i++)
        {
            if (key.Length > 0)
            {
                var read = await _signals.ReadAsync(key, ct).ConfigureAwait(false);
                if (!read.IsSuccess) return Result.Fail(read.Error, $"flow.waitUntil: {read.Message}");
                lhs = read.Value;
            }
            else
            {
                lhs = ctx.GetNumeric(variable);
            }
            if (TryCompare(lhs, op, rhs, out var met) && met)
            {
                _log(LogLevel.Info, $"flow.waitUntil: {label} ({lhs}) {op} {rhs}");
                return Result.Ok();
            }
            if (Environment.TickCount64 >= deadline) break;
            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }
        return Result.Fail(ErrorCode.Timeout,
            $"flow.waitUntil: {label} {op} {rhs} not met within {timeoutMs} ms (last {lhs}).");
    }

    // --- param helpers -------------------------------------------------------------------

    /// <summary>A numeric param, or <paramref name="fallback"/> when blank.</summary>
    private static double NumOr(WorkflowStep step, WorkflowContext ctx, string key, double fallback)
        => Has(step, key) ? Num(step, key, ctx) : fallback;

    /// <summary>A boolean param, or <paramref name="fallback"/> when blank.</summary>
    private static bool BoolOr(WorkflowStep step, WorkflowContext ctx, string key, bool fallback)
        => Has(step, key) ? Bool(step, key, ctx) : fallback;

    /// <summary>The step's <c>captureAs</c> prefix, or <paramref name="fallback"/> when blank.</summary>
    private static string CaptureAs(WorkflowStep step, WorkflowContext ctx, string fallback)
        => Has(step, "captureAs") ? Str(step, "captureAs", ctx).Trim() : fallback;
}
