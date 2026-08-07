using Horizon.Engine;
using Horizon.Workflow.HillClimb;

namespace Horizon.Workflow.RasterClimb;

/// <summary>
/// Z-stepped XY raster peak-hold search. Layer 2: drives a motion device through
/// <see cref="IHorizonEngine"/>'s motion facet and samples an injected
/// <see cref="IHillClimbFeedback"/>, at the same level as
/// <see cref="HillClimbEngine"/> and <see cref="SpiralScan.SpiralSearchEngine"/> —
/// pure code, no UI coupling; a front-end constructs it directly.
///
/// One iteration = <b>step Z+, search the XY plane, then move to the plane's
/// maximum</b>. The plane's verdict only exists once its scan has finished: no exit
/// is taken part-way through a plane, because near first light the signal at a given
/// Z is not knowable until the plane has been searched.
///
/// How much of the plane gets measured is <see cref="RasterClimbConfig.PlaneScan"/>:
/// <list type="bullet">
/// <item><see cref="RasterPlaneScan.Cross"/> (default) — a bidirectional line scan
/// along A, move to that axis's best, then the same along B. Each direction stops as
/// soon as it is past the peak, so a plane costs a handful of points. Assumes the
/// peak is roughly separable along the two axes.</item>
/// <item><see cref="RasterPlaneScan.Spiral"/> — a square spiral outward from the
/// plane centre over the full (2N+1)² grid, starting at the middle (where the signal
/// is most likely) with unit steps and no wasted travel. It assumes nothing about the
/// peak's shape; <see cref="RasterClimbConfig.RingsWithoutImprovementStop"/> still
/// stops it expanding once successive rings stop improving.</item>
/// </list>
///
/// The run stops on the first of: a plane's best reaching
/// <see cref="RasterClimbConfig.TargetReachedFraction"/> of
/// <see cref="RasterClimbConfig.TargetSagnacV"/> (70% of the SiPhOG target SAG power by
/// default); a plane's best collapsing more than
/// <see cref="RasterClimbConfig.ContactDropFraction"/> below the peak, which is read as
/// the FAU having hit the part; or the <see cref="RasterClimbConfig.MaxZTravel_um"/>
/// budget being spent. After any stop — including cancel or error — the stage returns to
/// the best point found across the whole run, so a suspected contact retreats to the last
/// known-good Z (fail-safe).
///
/// Feedback deliberately reuses <see cref="IHillClimbFeedback"/> rather than declaring
/// another identical seam: the Layer-3 head already supplies one (the SiPhOG
/// <c>SAG PWR (V)</c>) through <c>IProcessService.GetHillClimbFeedback</c>, so this
/// engine plugs into the existing wiring with no adapter.
/// </summary>
public sealed class RasterClimbEngine
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;

    private RasterClimbState _state = RasterClimbState.Idle;
    private readonly object _stateLock = new();

    // Per-run context
    private string _device = string.Empty;
    private IReadOnlyList<string> _axes = Array.Empty<string>();
    private IHillClimbFeedback _feedback = null!;
    private RasterClimbConfig _config = RasterClimbConfig.Default;

    // Live state during a run
    private double _vStart;
    private double _vRunBest;
    private double[]? _posRunBest;
    private double _bestZOffset_um;
    private double _bestOffsetA_um;
    private double _bestOffsetB_um;
    private double _zOffset_um;
    private int _planeIndex;
    private int _planesRastered;
    private int _pointsSampled;

    public event EventHandler<RasterClimbStateChangedEventArgs>? StateChanged;
    public event EventHandler<RasterClimbPointEventArgs>? PointSampled;
    public event EventHandler<RasterClimbPlaneEventArgs>? PlaneCompleted;
    public event EventHandler<RasterClimbProgressEventArgs>? ProgressUpdated;
    public event EventHandler<RasterClimbResult>? RunCompleted;
    public event EventHandler<Exception>? ErrorOccurred;

    public RasterClimbEngine(IHorizonEngine engine, Action<LogLevel, string>? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? ((_, _) => { });
    }

    public RasterClimbState CurrentState
    {
        get { lock (_stateLock) return _state; }
    }

    public bool IsRunning
    {
        get
        {
            var s = CurrentState;
            return s is RasterClimbState.SteppingZ
                or RasterClimbState.Rastering
                or RasterClimbState.MovingToPlaneBest
                or RasterClimbState.MovingToBest;
        }
    }

    /// <summary>
    /// Runs the raster climb on <paramref name="device"/>, maximising
    /// <paramref name="feedback"/>. Cancelling <paramref name="ct"/> halts the climb;
    /// the stage still returns to the best position found.
    /// </summary>
    public Task<RasterClimbResult> RunAsync(
        string device,
        IHillClimbFeedback feedback,
        RasterClimbConfig config,
        CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("RasterClimb is already running.");
        if (string.IsNullOrWhiteSpace(device)) throw new ArgumentException("Device required.", nameof(device));
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _config = (config ?? throw new ArgumentNullException(nameof(config))).Clone();
        _config.Validate();
        _device = device;

        return Task.Run(() => RunInternalAsync(ct), ct);
    }

    private async Task<RasterClimbResult> RunInternalAsync(CancellationToken ct)
    {
        var result = new RasterClimbResult { StartedAt = DateTime.Now, TargetV = _config.TargetSagnacV };

        try
        {
            var deviceInfo = _engine.Motion.GetDevice(_device)
                ?? throw new InvalidOperationException($"Motion device '{_device}' not found.");
            _axes = deviceInfo.Axes;
            int maxAxis = Math.Max(_config.AxisIndexZ, Math.Max(_config.AxisIndexA, _config.AxisIndexB));
            if (maxAxis >= _axes.Count)
                throw new InvalidOperationException(
                    $"Device '{_device}' has {_axes.Count} axes; the configured axis indices exceed that.");
            if (!_engine.Motion.IsConnected(_device))
                throw new InvalidOperationException($"Device '{_device}' is not connected.");
            if (!_feedback.HasSample)
                throw new InvalidOperationException($"Feedback '{_feedback.Description}' has no sample yet.");

            _vStart = await SampleAsync(ct).ConfigureAwait(false);
            _vRunBest = _vStart;
            _posRunBest = await ReadPositionAsync(ct).ConfigureAwait(false);
            _pointsSampled = 1;
            result.VStart = _vStart;

            _log(LogLevel.Info,
                $"RasterClimb start on '{_device}': V={_vStart:F6}, target={_config.TargetSagnacV:F6} V " +
                $"(exit at {_config.TargetReachedFraction:P0} = {ExitLevel:F6} V), " +
                $"Z step {_config.ZStep_um:0.###} µm × {_config.EffectiveZSteps} " +
                $"(budget {_config.MaxZTravel_um:0.#} µm), " +
                $"{_config.PlaneScan} scan {_config.XyStep_um:0.###} µm ±{_config.XyStepsPerSide}, " +
                $"contact guard {(_config.ContactDropFraction > 0 ? $"-{_config.ContactDropFraction:P0} of peak" : "off")} " +
                $"[{_feedback.Description}].");

            var stopReason = await ClimbAsync(ct).ConfigureAwait(false);

            // Park on the best point of the whole run (which is the last plane's best on a
            // target exit, and the historical peak when the Z budget ran out).
            SetState(RasterClimbState.MovingToBest);
            await MoveToAbsoluteAsync(_posRunBest!, ct).ConfigureAwait(false);

            FillResult(result, stopReason);
            result.Message =
                $"Stopped: {stopReason}. Best {_vRunBest:F6} V ({result.TargetFraction:P1} of target) " +
                $"at Z{_bestZOffset_um:+0.0;-0.0} µm, XY {_bestOffsetA_um:+0.0;-0.0}/{_bestOffsetB_um:+0.0;-0.0} µm.";
            _log(LogLevel.Info, $"RasterClimb: {result.Message}");

            SetState(RasterClimbState.Completed);
            RunCompleted?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            FillResult(result, RasterClimbStopReason.Cancelled);
            result.Message = "Cancelled.";
            await ReturnToBestAfterAbortAsync().ConfigureAwait(false);

            SetState(RasterClimbState.Cancelled);
            RunCompleted?.Invoke(this, result);
            return result;
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, $"RasterClimb error: {ex.Message}");
            FillResult(result, RasterClimbStopReason.Error);
            result.Message = ex.Message;
            await ReturnToBestAfterAbortAsync().ConfigureAwait(false);

            SetState(RasterClimbState.Error);
            ErrorOccurred?.Invoke(this, ex);
            RunCompleted?.Invoke(this, result);
            return result;
        }
    }

    /// <summary>Signal level that ends the run (target × fraction).</summary>
    private double ExitLevel => _config.TargetSagnacV * _config.TargetReachedFraction;

    /// <summary>
    /// The climb proper: optionally scan the starting plane, then repeat
    /// "step Z → scan the plane → move to its max" until the target is reached, the signal
    /// collapses (suspected contact), or the Z travel budget is spent. The loop is bounded
    /// by <see cref="RasterClimbConfig.EffectiveZSteps"/> — a fixed count, Power of 10 Rule 2.
    /// </summary>
    private async Task<RasterClimbStopReason> ClimbAsync(CancellationToken ct)
    {
        if (_config.RasterStartPlane)
        {
            var first = await RasterPlaneAsync(ct).ConfigureAwait(false);
            if (first != PlaneOutcome.Continue) return ToStopReason(first);
        }

        int budget = _config.EffectiveZSteps;
        for (int step = 0; step < budget; step++)
        {
            ct.ThrowIfCancellationRequested();

            SetState(RasterClimbState.SteppingZ);
            double dzMm = _config.ZSign * _config.ZStep_um / 1000.0;
            await MoveAxisAsync(_config.AxisIndexZ, dzMm, ct).ConfigureAwait(false);
            _zOffset_um += _config.ZSign * _config.ZStep_um;
            if (_config.ZSettleMs > 0)
                await Task.Delay(_config.ZSettleMs, ct).ConfigureAwait(false);

            var outcome = await RasterPlaneAsync(ct).ConfigureAwait(false);
            if (outcome != PlaneOutcome.Continue) return ToStopReason(outcome);
        }

        _log(LogLevel.Warning,
            $"RasterClimb: Z budget spent ({budget} × {_config.ZStep_um:0.###} µm = " +
            $"{budget * _config.ZStep_um:0.#} µm) without reaching {ExitLevel:F6} V; " +
            $"best was {_vRunBest:F6} V.");
        return RasterClimbStopReason.ZBudgetSpent;
    }

    private static RasterClimbStopReason ToStopReason(PlaneOutcome outcome) => outcome switch
    {
        PlaneOutcome.TargetReached => RasterClimbStopReason.TargetReached,
        PlaneOutcome.SignalDropped => RasterClimbStopReason.SignalDropped,
        _ => RasterClimbStopReason.ZBudgetSpent,
    };

    /// <summary>What a completed plane tells the climb to do next.</summary>
    private enum PlaneOutcome
    {
        Continue,
        TargetReached,
        /// <summary>The plane's best collapsed below the peak — suspected contact.</summary>
        SignalDropped,
    }

    /// <summary>
    /// Searches the current Z plane for its maximum and moves there, then judges the plane:
    /// target reached, signal collapsed (suspected contact), or carry on. Whichever strategy
    /// runs, the plane is only judged once its scan has finished — no exit is taken part-way
    /// through a plane, since a single point mid-scan says nothing about the plane.
    /// </summary>
    private async Task<PlaneOutcome> RasterPlaneAsync(CancellationToken ct)
    {
        SetState(RasterClimbState.Rastering);

        var plane = new PlaneScanState();
        if (_config.PlaneScan == RasterPlaneScan.Cross)
            await ScanPlaneCrossAsync(plane, ct).ConfigureAwait(false);
        else
            await ScanPlaneSpiralAsync(plane, ct).ConfigureAwait(false);

        if (plane.BestPos == null)
            throw new InvalidOperationException("Plane scan completed without a best point.");

        // Only now is the plane's signal known — go to its maximum.
        SetState(RasterClimbState.MovingToPlaneBest);
        await MoveToAbsoluteAsync(plane.BestPos, ct).ConfigureAwait(false);

        _planesRastered++;

        // Suspected contact: the plane's best collapsed well below the peak. Judge it against
        // the peak BEFORE folding this plane in, and only once a positive peak exists — a
        // relative drop means nothing while the signal is still at/below zero.
        bool contact = _config.ContactDropFraction > 0
            && _planesRastered > 1
            && _vRunBest > 0
            && plane.BestV < _vRunBest - Math.Abs(_vRunBest) * _config.ContactDropFraction;

        if (plane.BestV > _vRunBest)
        {
            _vRunBest = plane.BestV;
            _posRunBest = (double[])plane.BestPos.Clone();
            _bestZOffset_um = _zOffset_um;
            _bestOffsetA_um = plane.BestIa * _config.XyStep_um;
            _bestOffsetB_um = plane.BestIb * _config.XyStep_um;
        }

        double fraction = _config.TargetSagnacV > 0 ? plane.BestV / _config.TargetSagnacV : 0;
        _log(LogLevel.Info,
            $"RasterClimb plane {_planeIndex} (Z{_zOffset_um:+0.0;-0.0} µm, {_config.PlaneScan}): " +
            $"best {plane.BestV:F6} V ({fraction:P1} of target) at XY " +
            $"{plane.BestIa * _config.XyStep_um:+0.0;-0.0}/{plane.BestIb * _config.XyStep_um:+0.0;-0.0} µm, " +
            $"{plane.Sampled} points.");

        PlaneCompleted?.Invoke(this, new RasterClimbPlaneEventArgs(
            _planeIndex, _zOffset_um, plane.BestV,
            plane.BestIa * _config.XyStep_um, plane.BestIb * _config.XyStep_um, _vRunBest, fraction));

        _planeIndex++;

        if (plane.BestV >= ExitLevel)
            return PlaneOutcome.TargetReached;

        if (contact)
        {
            _log(LogLevel.Warning,
                $"RasterClimb: plane best {plane.BestV:F6} V is {(_vRunBest - plane.BestV) / Math.Abs(_vRunBest):P1} " +
                $"below the peak {_vRunBest:F6} V (limit {_config.ContactDropFraction:P0}) at Z{_zOffset_um:+0.0;-0.0} µm — " +
                "suspected contact with the part. Stopping and retreating to the peak.");
            return PlaneOutcome.SignalDropped;
        }

        return PlaneOutcome.Continue;
    }

    /// <summary>
    /// Full square spiral outward from the plane centre, sampling every cell of the
    /// (2N+1)² grid. When <see cref="RasterClimbConfig.RingsWithoutImprovementStop"/> is set,
    /// the sweep stops expanding once that many consecutive rings fail to improve the plane's
    /// best — the signal falls off with radius, so once it does the outer cells are known to
    /// be worse and sampling them is wasted travel.
    /// </summary>
    private async Task ScanPlaneSpiralAsync(PlaneScanState plane, CancellationToken ct)
    {
        double stepMm = _config.XyStep_um / 1000.0;
        int n = _config.XyStepsPerSide;
        int total = (2 * n + 1) * (2 * n + 1);

        int ia = 0, ib = 0;          // grid offset from the plane centre, in cells
        int legLen = 1;              // cells left in the current spiral leg
        int dir = 0;                 // 0:+A 1:+B 2:-A 3:-B
        int legsAtLen = 0;           // two legs per length, then the length grows
        int visited = 0;
        int ring = 0;                // max(|ia|,|ib|) of the ring being swept
        int ringsWithoutImprovement = 0;
        bool ringImproved = false;

        // The loop is bounded by the grid size: the square spiral covers the (2n+1)² box
        // exactly, and 'visited' is the statically-known termination counter.
        while (visited < total)
        {
            ct.ThrowIfCancellationRequested();

            int cellRing = Math.Max(Math.Abs(ia), Math.Abs(ib));
            if (cellRing != ring)
            {
                // A ring just closed — decide whether the next one is worth sweeping.
                if (_config.RingsWithoutImprovementStop > 0)
                {
                    ringsWithoutImprovement = ringImproved ? 0 : ringsWithoutImprovement + 1;
                    if (ringsWithoutImprovement >= _config.RingsWithoutImprovementStop)
                    {
                        _log(LogLevel.Debug,
                            $"RasterClimb plane {_planeIndex}: past peak at ring {ring} " +
                            $"({ringsWithoutImprovement} rings without improvement) — {visited}/{total} points swept.");
                        return;
                    }
                }
                ring = cellRing;
                ringImproved = false;
            }

            if (await SamplePlanePointAsync(plane, ia, ib, ct).ConfigureAwait(false))
                ringImproved = true;
            visited++;
            if (visited >= total) return;

            // Next spiral cell. Legs run 1,1,2,2,3,3,… so the path is a square spiral
            // outward from the centre; within (2n+1)² samples it never leaves the box.
            (ia, ib) = Advance(ia, ib, dir);
            bool alongA = dir is 0 or 2;
            bool positive = dir is 0 or 1;
            await MoveAxisAsync(alongA ? _config.AxisIndexA : _config.AxisIndexB,
                positive ? stepMm : -stepMm, ct).ConfigureAwait(false);

            if (--legLen == 0)
            {
                dir = (dir + 1) & 3;
                legsAtLen++;
                legLen = (legsAtLen / 2) + 1;
            }
        }
    }

    /// <summary>
    /// Cross scan: a bidirectional line scan along A from the plane centre, a move to that
    /// axis's best, then the same along B (and optionally more passes). Each direction walks
    /// out only until the signal is past its peak — <see cref="RasterClimbConfig.PeakDropCount"/>
    /// consecutive samples that fail to beat the axis best — so a plane costs a handful of
    /// points instead of the whole grid. The trade: it assumes the peak is roughly separable
    /// along the two axes, which a full spiral does not.
    /// </summary>
    private async Task ScanPlaneCrossAsync(PlaneScanState plane, CancellationToken ct)
    {
        // The centre is shared by every line, so sample it once up front.
        await SamplePlanePointAsync(plane, 0, 0, ct).ConfigureAwait(false);

        for (int pass = 0; pass < _config.CrossPasses; pass++)
        {
            await ScanLineAsync(plane, alongA: true, ct).ConfigureAwait(false);
            await ScanLineAsync(plane, alongA: false, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One bidirectional line scan through the plane's current best, along A or B. Walks
    /// outward in +1 then −1, stopping each direction at the grid edge or once the signal is
    /// past its peak, and leaves the stage on the line's best point.
    /// </summary>
    private async Task ScanLineAsync(PlaneScanState plane, bool alongA, CancellationToken ct)
    {
        int axisIndex = alongA ? _config.AxisIndexA : _config.AxisIndexB;
        double stepMm = _config.XyStep_um / 1000.0;
        int n = _config.XyStepsPerSide;

        // The line runs through wherever the plane's best currently sits.
        int fixedCell = alongA ? plane.BestIb : plane.BestIa;
        int lineOrigin = alongA ? plane.BestIa : plane.BestIb;
        double[] originPos = (double[])plane.BestPos!.Clone();

        foreach (int sign in new[] { +1, -1 })
        {
            ct.ThrowIfCancellationRequested();

            // Every direction starts from the line's origin point (already sampled).
            await MoveToAbsoluteAsync(originPos, ct).ConfigureAwait(false);

            double lineBest = plane.BestV;
            int drops = 0;
            // Bounded by the grid half-width — Power of 10 Rule 2.
            for (int k = 1; k <= 2 * n; k++)
            {
                int cell = lineOrigin + sign * k;
                if (Math.Abs(cell) > n) break;

                await MoveAxisAsync(axisIndex, sign * stepMm, ct).ConfigureAwait(false);

                int ia = alongA ? cell : fixedCell;
                int ib = alongA ? fixedCell : cell;
                await SamplePlanePointAsync(plane, ia, ib, ct).ConfigureAwait(false);
                double v = plane.LastV;

                if (Improves(v, lineBest))
                {
                    lineBest = v;
                    drops = 0;
                }
                else if (++drops >= _config.PeakDropCount)
                {
                    // Past the peak in this direction — the rest of the line is downhill.
                    break;
                }
            }
        }

        // Park on the best point the line found (which may still be its origin).
        await MoveToAbsoluteAsync(plane.BestPos!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles, samples the point the stage is standing on, records it if it is the plane's
    /// new best, and raises the point/progress events. Returns true when it became the best.
    /// </summary>
    private async Task<bool> SamplePlanePointAsync(PlaneScanState plane, int ia, int ib, CancellationToken ct)
    {
        if (_config.SettleMs > 0)
            await Task.Delay(_config.SettleMs, ct).ConfigureAwait(false);

        double v = await SampleAsync(ct).ConfigureAwait(false);
        _pointsSampled++;
        plane.Sampled++;
        plane.LastV = v;

        // The threshold keeps sensor noise from claiming a new best (and so keeps ties with
        // the point nearer the centre, which is the safer place to stand).
        bool isBest = plane.BestPos == null || Improves(v, plane.BestV);
        if (isBest)
        {
            plane.BestV = v;
            plane.BestPos = await ReadPositionAsync(ct).ConfigureAwait(false);
            plane.BestIa = ia;
            plane.BestIb = ib;
        }

        PointSampled?.Invoke(this, new RasterClimbPointEventArgs(
            _planeIndex, plane.Sampled - 1, ia * _config.XyStep_um, ib * _config.XyStep_um, v, isBest));
        ProgressUpdated?.Invoke(this, new RasterClimbProgressEventArgs(
            _planeIndex, plane.Sampled, _config.PointsPerRaster, _zOffset_um, v, plane.BestV, _vRunBest));

        return isBest;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> beats <paramref name="reference"/> by more than
    /// the noise margin. The margin is scaled by |reference| rather than written as
    /// <c>reference × (1 + threshold)</c>, because the Sagnac signal is signed: with a negative
    /// reference the multiplicative form moves the bar the wrong way (it would accept a
    /// <i>worse</i> value), which near first light is exactly where this search operates.
    /// </summary>
    private bool Improves(double candidate, double reference)
        => candidate > reference + Math.Abs(reference) * _config.ImprovementThreshold;

    /// <summary>Advance a grid offset by one cell in direction <paramref name="dir"/> (0:+A 1:+B 2:-A 3:-B).</summary>
    private static (int ia, int ib) Advance(int ia, int ib, int dir) => dir switch
    {
        0 => (ia + 1, ib),
        1 => (ia, ib + 1),
        2 => (ia - 1, ib),
        _ => (ia, ib - 1),
    };

    /// <summary>Running state of one plane's scan, shared by both strategies.</summary>
    private sealed class PlaneScanState
    {
        public double BestV { get; set; } = double.NegativeInfinity;
        public double[]? BestPos { get; set; }
        public int BestIa { get; set; }
        public int BestIb { get; set; }
        public double LastV { get; set; }
        public int Sampled { get; set; }
    }

    /// <summary>
    /// Best-effort return to the run's best position after a cancel or error, on a fresh
    /// token so the abort itself can't prevent the stage from parking somewhere known.
    /// </summary>
    private async Task ReturnToBestAfterAbortAsync()
    {
        try
        {
            if (_posRunBest != null && _engine.Motion.IsConnected(_device))
            {
                SetState(RasterClimbState.MovingToBest);
                await MoveToAbsoluteAsync(_posRunBest, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log(LogLevel.Warning, $"RasterClimb: failed to return to best after abort: {ex.Message}");
        }
    }

    private async Task<double> SampleAsync(CancellationToken ct)
    {
        int n = _config.SamplesPerPoint;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            sum += ReadFeedbackChecked();
            if (i < n - 1) await Task.Delay(_config.SampleDelayMs, ct).ConfigureAwait(false);
        }
        return sum / n;
    }

    /// <summary>
    /// Reads the feedback and validates it is finite (Power of 10 Rule 7 — validate inputs
    /// at the boundary). A NaN/∞ from the sensor would poison the max comparison (∞ would
    /// falsely win and park the stage there), so we fail fast; the run's handler then
    /// returns the stage to the last known best.
    /// </summary>
    private double ReadFeedbackChecked()
    {
        double v = _feedback.Read();
        if (!double.IsFinite(v))
            throw new InvalidOperationException(
                $"Feedback '{_feedback.Description}' returned a non-finite value ({v}).");
        return v;
    }

    private async Task MoveAxisAsync(int axisIndex, double deltaMm, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string axis = _axes[axisIndex];
        string what = $"Move {axis} {deltaMm * 1000.0:+0.000;-0.000} µm";
        var move = await WithTimeoutAsync(
            token => _engine.Motion.MoveRelativeAsync(_device, axis, deltaMm, token), what, ct).ConfigureAwait(false);
        if (!move.IsSuccess)
            throw new InvalidOperationException($"{what} failed: {move.Message}");
    }

    private async Task MoveToAbsoluteAsync(double[] pos, CancellationToken ct)
    {
        var move = await WithTimeoutAsync(
            token => _engine.Motion.MoveToPositionAsync(_device, ToPosition(pos), token), "Move to best", ct)
            .ConfigureAwait(false);
        if (!move.IsSuccess)
            throw new InvalidOperationException($"Move to best failed: {move.Message}");
    }

    private async Task<double[]> ReadPositionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // GetPositionAsync has no token, so race it against the timeout explicitly.
        var pos = await WithTimeoutAsync(_ => _engine.Motion.GetPositionAsync(_device), "Read position", ct)
            .ConfigureAwait(false);
        if (!pos.IsSuccess)
            throw new InvalidOperationException($"Read position failed: {pos.Message}");
        var c = pos.Value.Components;
        return new[] { c[0], c[1], c[2], c[3], c[4], c[5] };
    }

    /// <summary>
    /// Runs a hardware operation under a per-op time bound (<see cref="RasterClimbConfig.IoTimeoutMs"/>).
    /// Power of 10 Rule 2 in the time domain: every I/O completes within a fixed bound, so a hung
    /// controller or feedback link can't stall the climb forever despite the step cap. A timeout
    /// surfaces as <see cref="TimeoutException"/> (caught by the run's handler → returns to the last
    /// best); a caller-requested cancel still propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> op, string what, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = op(linked.Token);
        // Race against a delay so the bound holds even for ops that ignore the token.
        var timeout = Task.Delay(_config.IoTimeoutMs, linked.Token);
        var winner = await Task.WhenAny(task, timeout).ConfigureAwait(false);
        if (winner == task)
        {
            linked.Cancel();                            // stop the timeout delay
            return await task.ConfigureAwait(false);    // observe the result / rethrow faults
        }

        ct.ThrowIfCancellationRequested();              // caller cancel → OperationCanceledException
        linked.Cancel();                                // best-effort: cancel the (token-aware) op
        throw new TimeoutException($"{what} did not complete within {_config.IoTimeoutMs} ms.");
    }

    private static MotionPosition ToPosition(double[] p)
        => new(p[0], p[1], p[2], p[3], p[4], p[5]);

    private void FillResult(RasterClimbResult result, RasterClimbStopReason stopReason)
    {
        result.StopReason = stopReason;
        result.VStart = _vStart;
        result.VBest = _vRunBest;
        result.PosBest = _posRunBest != null ? ToPosition(_posRunBest) : null;
        result.BestZOffset_um = _bestZOffset_um;
        result.BestOffsetA_um = _bestOffsetA_um;
        result.BestOffsetB_um = _bestOffsetB_um;
        result.ZTravelled_um = _zOffset_um;
        result.PlanesRastered = _planesRastered;
        result.PointsSampled = _pointsSampled;
        result.TargetV = _config.TargetSagnacV;
        result.TargetFraction = _config.TargetSagnacV > 0 ? _vRunBest / _config.TargetSagnacV : 0;
        result.FinishedAt = DateTime.Now;
    }

    private void SetState(RasterClimbState newState)
    {
        RasterClimbState old;
        lock (_stateLock)
        {
            old = _state;
            if (old == newState) return;
            _state = newState;
        }
        StateChanged?.Invoke(this, new RasterClimbStateChangedEventArgs(old, newState));
        _log(LogLevel.Debug, $"RasterClimb state: {old} -> {newState}");
    }
}
