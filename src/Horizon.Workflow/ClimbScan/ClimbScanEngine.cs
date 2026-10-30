using System.Globalization;
using Horizon.Engine;
using Horizon.Workflow.Scanning;

namespace Horizon.Workflow.ClimbScan;

/// <summary>
/// Climb scan — gap-closing search ported from FireDog's <c>climb_raster.lua</c> (see
/// <c>docs/dlam-ui.md</c> §Climb scan and FireDog's <c>docs/climb-raster-design.md</c>). One
/// iteration = step Z toward the part, probe once where the stage stands, and — unless the
/// probe shows Z alone is still paying — search the XY plane (cross or spiral) and move to its
/// maximum. It stops on the first of: a plane best (or probe) reaching <c>TargetPct</c> of the
/// target; a plane best collapsing <c>ContactDropPct</c> under the peak (suspected contact); the
/// Z budget; optionally a stall. On EVERY stop — target, contact, budget, an error or a STOP —
/// the stage returns to the best point of the whole run (a STOP on a fresh, bounded token:
/// Horizon's active-alignment rule; FireDog skipped the retreat on a STOP).
/// <para>Moves to a remembered point retreat Z FIRST when the point lies back from the part,
/// and advance Z LAST otherwise, so a retreat never drags XY across the part.</para>
/// <para>The caller owns the feedback source (started before, stopped after).</para>
/// </summary>
public sealed class ClimbScanEngine
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;

    public ClimbScanEngine(IHorizonEngine engine, Action<LogLevel, string>? log = null)
    {
        _engine = engine;
        _log = log ?? ((_, _) => { });
    }

    public async Task<ClimbScanResult> RunAsync(
        string device, IScanFeedbackSource feedback, ClimbScanParameters parameters, CancellationToken ct)
    {
        var valid = parameters.Validate();
        if (!valid.IsSuccess) return new ClimbScanResult { Message = valid.Message, Target = parameters.Target };
        if (_engine.Motion.GetDevice(device) == null)
            return new ClimbScanResult { Message = $"Climb scan: motion device '{device}' not found.", Target = parameters.Target };
        if (!_engine.Motion.IsConnected(device))
            return new ClimbScanResult { Message = $"Climb scan: motion device '{device}' is not connected.", Target = parameters.Target };

        var run = new ClimbRun(_engine.Motion, device, feedback, parameters, _log);
        return await run.ExecuteAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>One climb's state — the Lua script's locals.</summary>
internal sealed class ClimbRun
{
    private const int FailSafeTimeoutMs = 30_000;
    private const int BaselineSpacingMs = 50;
    private const double SameMm = 1e-6;   // sub-nm: already there

    private readonly IMotionFacet _motion;
    private readonly ScanAxisMotion _m;
    private readonly IScanFeedbackSource _fb;
    private readonly ClimbScanParameters _p;
    private readonly Action<LogLevel, string> _log;
    private readonly string _a, _b, _z;
    private readonly double _exitLevel;
    private readonly long _t0 = Environment.TickCount64;

    private double _zTravelMm;
    private int _planeIdx, _planesRastered, _planesSkipped, _points, _stall;
    private double? _vAtPoint;          // last value measured where the stage stands: the probe's reference
    private RunBest _best = new(double.NegativeInfinity, null, 0, 0, 0);
    private bool _settleWarned;
    private int _traceSeq;
    private double? _traceLastV;
    private string _phase = "base";

    private sealed record RunBest(double V, MotionPosition? Pose, double ZMm, double AMm, double BMm);

    /// <summary>A plane's running best — shared by the cross and spiral searches.</summary>
    private sealed class Plane
    {
        public double BestV;
        public MotionPosition? BestPose;
        public int Bia, Bib, Sampled;
    }

    private enum Verdict { Continue, Target, Contact, Stall }

    public ClimbRun(IMotionFacet motion, string device, IScanFeedbackSource feedback,
                    ClimbScanParameters parameters, Action<LogLevel, string> log)
    {
        _motion = motion;
        _m = new ScanAxisMotion(motion, device, parameters.MoveTimeoutMs);
        _fb = feedback;
        _p = parameters;
        _log = log;
        _a = parameters.PlaneAxisA.Trim().ToUpperInvariant();
        _b = parameters.PlaneAxisB.Trim().ToUpperInvariant();
        _z = parameters.ZAxis.Trim().ToUpperInvariant();
        _exitLevel = parameters.Target * parameters.TargetPct / 100.0;
    }

    private int ZBudgetSteps => Math.Max(1, Math.Min(_p.MaxZSteps, (int)Math.Floor(_p.MaxZMm / _p.ZStepMm + 1e-9)));

    public async Task<ClimbScanResult> ExecuteAsync(CancellationToken ct)
    {
        bool spiral = _p.PlaneScan == ClimbPlaneScan.Spiral;
        int n = _p.XySteps;
        int ptsPerPlane = spiral ? (2 * n + 1) * (2 * n + 1) : 1 + _p.CrossPasses * 8 * n;
        _log(LogLevel.Info,
            $"Climb scan: {_m.Device} on {_fb.Description}, {_z}{(_p.ZDir > 0 ? "+" : "-")} {Um(_p.ZStepMm):0.###} µm × ≤{ZBudgetSteps} " +
            $"(budget {Um(_p.MaxZMm):0.###} µm), {(spiral ? "spiral" : "cross")} plane {_a}/{_b} {Um(_p.XyStepMm):0.###} µm ±{n} (≤{ptsPerPlane} pts), " +
            $"target {_p.Target:G4} → exit {_p.TargetPct:G}% ({_exitLevel:G4})");
        _log(LogLevel.Info,
            "  Z-probe fast path: " + (_p.ZProbePct > 0
                ? $"skip the XY search while a step gains > {_p.ZProbePct:G}% (max {(_p.MaxSkips > 0 ? _p.MaxSkips.ToString(CultureInfo.InvariantCulture) : "unlimited")} in a row)"
                : "off (every plane searched)") +
            "; contact guard: " + (_p.ContactDropPct > 0 ? $"-{_p.ContactDropPct:G}% of peak" : "off"));

        double baseline = 0;
        try
        {
            if (_p.VelocityMmPerSec > 0)
            {
                var v = await _motion.SetDeviceVelocityAsync(_m.Device, _p.VelocityMmPerSec).ConfigureAwait(false);
                if (!v.IsSuccess) return Result(false, $"Climb scan: set scan velocity failed — {v.Message}", ClimbStop.Aborted, 0, 0);
            }
            if (_p.Trace)
                _log(LogLevel.Info, $"clmb,header: seq,t_s,phase,plane,z_um,ia,ib,{_a},{_b},{_z},value,dv,note");

            // Averaged baseline at the start pose — the only averaging in the climb.
            double sum = 0;
            for (int i = 0; i < _p.AvgCount; i++)
            {
                double v = await MeasureAsync(ct).ConfigureAwait(false);
                _points++;
                await TraceAsync(v, 0, 0, "baseline").ConfigureAwait(false);
                sum += v;
                if (i < _p.AvgCount - 1) await Task.Delay(BaselineSpacingMs, ct).ConfigureAwait(false);
            }
            baseline = sum / _p.AvgCount;
            _vAtPoint = baseline;
            _best = new RunBest(baseline, await _m.PoseAsync().ConfigureAwait(false), 0, 0, 0);
            _log(LogLevel.Info, $"Climb baseline = {baseline:G4} {_fb.Unit}");

            var stop = await ClimbAsync(ct).ConfigureAwait(false);

            await GoToAsync(_best.Pose!, ct).ConfigureAwait(false);
            _phase = "final";
            double finalV = await MeasureAsync(ct).ConfigureAwait(false);
            _points++;
            await TraceAsync(finalV, 0, 0, "final@peak").ConfigureAwait(false);

            return Result(true,
                $"Climb scan done ({stop}): baseline {baseline:G4}, best {_best.V:G4}, final {finalV:G4} " +
                $"({100.0 * _best.V / _p.Target:F1}% of {_p.Target:G4}) at {_z}{Um(_best.ZMm):+0.0;-0.0} µm, " +
                $"{_a}{Um(_best.AMm):+0.0;-0.0}/{_b}{Um(_best.BMm):+0.0;-0.0} µm; {_planesRastered} planes searched + " +
                $"{_planesSkipped} skipped, {_points} points, Z travel {Um(_zTravelMm):+0.0;-0.0} µm",
                stop, baseline, finalV);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ParkAsync("stopped").ConfigureAwait(false);
            return Result(false, "Climb scan stopped — returned to the best point of the run.", ClimbStop.Aborted, baseline, _best.V)
                with { Halted = true };
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, $"Climb scan error: {ex.Message}");
            await ParkAsync("error").ConfigureAwait(false);
            return Result(false, $"Climb scan failed: {ex.Message}", ClimbStop.Aborted, baseline, _best.V);
        }
    }

    /// <summary>Optional start-plane search, then "step Z → probe → (search the plane → go to its max)", bounded by a fixed step count.</summary>
    private async Task<ClimbStop> ClimbAsync(CancellationToken ct)
    {
        int skipRun = 0;
        if (_p.RasterStart)
        {
            var first = await RasterPlaneAsync(null, ct).ConfigureAwait(false);
            if (first != Verdict.Continue) return ToStop(first);
        }

        int budget = ZBudgetSteps;
        for (int i = 0; i < budget; i++)
        {
            await _m.MoveRelativeAsync(_z, _p.ZDir * _p.ZStepMm, ct).ConfigureAwait(false);
            _zTravelMm += _p.ZDir * _p.ZStepMm;
            if (_p.ZSettleMs > 0) await Task.Delay(_p.ZSettleMs, ct).ConfigureAwait(false);

            double? seed = null;
            bool skipped = false;
            if (_p.ZProbePct > 0)
            {
                var (verdict, v) = await ProbeAfterZAsync(skipRun, ct).ConfigureAwait(false);
                if (verdict == ProbeVerdict.Target) return ClimbStop.Target;
                if (verdict == ProbeVerdict.Skip)
                {
                    skipRun++;
                    skipped = true;
                }
                else
                {
                    skipRun = 0;
                    seed = v;   // the probe IS the plane's centre sample
                }
            }

            if (!skipped)
            {
                var outcome = await RasterPlaneAsync(seed, ct).ConfigureAwait(false);
                if (outcome != Verdict.Continue) return ToStop(outcome);
            }
        }

        _log(LogLevel.Info,
            $"Z budget spent ({budget} × {Um(_p.ZStepMm):0.###} µm = {Um(budget * _p.ZStepMm):0.###} µm) without reaching {_exitLevel:G4} — best was {_best.V:G4}");
        return ClimbStop.ZBudget;
    }

    private static ClimbStop ToStop(Verdict v) => v switch
    {
        Verdict.Target => ClimbStop.Target,
        Verdict.Contact => ClimbStop.Contact,
        _ => ClimbStop.Stall,
    };

    private enum ProbeVerdict { Target, Skip, Raster }

    /// <summary>
    /// One sample straight after a Z step, at the XY the stage already stands on — the same point,
    /// one step deeper. It can end the run only by REACHING THE TARGET, never on a drop (a fall is
    /// exactly what hands over to a plane search, the only thing entitled to call a contact).
    /// </summary>
    private async Task<(ProbeVerdict Verdict, double V)> ProbeAfterZAsync(int skipRun, CancellationToken ct)
    {
        _phase = "probe";
        double? reference = _vAtPoint;
        double v = await MeasureAsync(ct).ConfigureAwait(false);
        _points++;
        _vAtPoint = v;

        // A probe is a real measured point: fold it in so the fail-safe park has it.
        if (v > _best.V) _best = new RunBest(v, await _m.PoseAsync().ConfigureAwait(false), _zTravelMm, 0, 0);

        if (v >= _exitLevel)
        {
            await TraceAsync(v, 0, 0, "probe-target").ConfigureAwait(false);
            _log(LogLevel.Info, $"  probe at {_z}{Um(_zTravelMm):+0.0;-0.0} µm: {v:G4} reached the exit level {_exitLevel:G4} without an XY search");
            _planeIdx++;
            return (ProbeVerdict.Target, v);
        }

        // Only a positive reference can grow by a fraction; at or below zero every plane is searched.
        bool paying = reference is > 0 && v > reference.Value + reference.Value * _p.ZProbePct / 100.0;
        if (!paying)
        {
            await TraceAsync(v, 0, 0, "probe").ConfigureAwait(false);
            return (ProbeVerdict.Raster, v);
        }

        if (_p.MaxSkips > 0 && skipRun >= _p.MaxSkips)
        {
            // Z is still paying, but a Z move is not perfectly decoupled from the plane (pivot,
            // cross-talk): an unwatched run of skips lets XY error accumulate behind the gain test.
            await TraceAsync(v, 0, 0, "probe-capped").ConfigureAwait(false);
            _log(LogLevel.Info, $"  plane {_planeIdx} ({_z}{Um(_zTravelMm):+0.0;-0.0} µm): {skipRun} planes skipped in a row — searching the plane anyway");
            return (ProbeVerdict.Raster, v);
        }

        _planesSkipped++;
        await TraceAsync(v, 0, 0, "probe-skip").ConfigureAwait(false);
        _log(LogLevel.Info,
            $"  plane {_planeIdx} ({_z}{Um(_zTravelMm):+0.0;-0.0} µm): probe {reference:G4} → {v:G4} " +
            $"(+{100.0 * (v - reference!.Value) / reference.Value:F1}%) — Z is still paying, skipping the XY search");
        _planeIdx++;
        return (ProbeVerdict.Skip, v);
    }

    /// <summary>Searches the current plane, moves to its maximum, then judges the plane. <paramref name="seed"/> = the probe at the centre.</summary>
    private async Task<Verdict> RasterPlaneAsync(double? seed, CancellationToken ct)
    {
        _phase = "plane";
        var plane = new Plane();
        if (seed is { } s)
        {
            plane.BestV = s;
            plane.BestPose = await _m.PoseAsync().ConfigureAwait(false);
            plane.Sampled = 1;
        }
        else
        {
            await SamplePointAsync(plane, 0, 0, ct).ConfigureAwait(false);
        }

        if (_p.PlaneScan == ClimbPlaneScan.Spiral)
            await ScanSpiralAsync(plane, ct).ConfigureAwait(false);
        else
            for (int pass = 0; pass < _p.CrossPasses; pass++)
            {
                await ScanLineAsync(plane, alongA: true, ct).ConfigureAwait(false);
                await ScanLineAsync(plane, alongA: false, ct).ConfigureAwait(false);
            }

        // Only now is the plane's signal known — go to its maximum.
        await GoToAsync(plane.BestPose!, ct).ConfigureAwait(false);
        _planesRastered++;
        _vAtPoint = plane.BestV;

        // Contact: judged against the peak BEFORE folding this plane in, and only once a
        // positive peak exists. Skipped planes count as prior measurement.
        bool contact = _p.ContactDropPct > 0
                       && _planesRastered + _planesSkipped > 1
                       && _best.V > 0
                       && plane.BestV < _best.V - Math.Abs(_best.V) * _p.ContactDropPct / 100.0;

        bool gained = plane.BestV > _best.V;
        if (gained)
            _best = new RunBest(plane.BestV, plane.BestPose, _zTravelMm, plane.Bia * _p.XyStepMm, plane.Bib * _p.XyStepMm);

        _log(LogLevel.Info,
            $"  plane {_planeIdx} ({_z}{Um(_zTravelMm):+0.0;-0.0} µm, {_p.PlaneScan.ToString().ToLowerInvariant()}): best={plane.BestV:G4} " +
            $"({100.0 * plane.BestV / _p.Target:F1}% of target) at {_a}{Um(plane.Bia * _p.XyStepMm):+0.0;-0.0}/" +
            $"{_b}{Um(plane.Bib * _p.XyStepMm):+0.0;-0.0} µm, {plane.Sampled} pts");
        _planeIdx++;

        if (plane.BestV >= _exitLevel) return Verdict.Target;
        if (contact)
        {
            _log(LogLevel.Warning,
                $"  plane best {plane.BestV:G4} is {100.0 * (_best.V - plane.BestV) / Math.Abs(_best.V):F1}% below the peak {_best.V:G4} " +
                $"(limit {_p.ContactDropPct:G}%) at {_z}{Um(_zTravelMm):+0.0;-0.0} µm — suspected contact, retreating");
            return Verdict.Contact;
        }
        if (_p.StallPlanes > 0)
        {
            _stall = gained ? 0 : _stall + 1;
            if (_stall >= _p.StallPlanes)
            {
                _log(LogLevel.Info, $"  {_stall} planes without a new peak — stopping at the peak {_best.V:G4}");
                return Verdict.Stall;
            }
        }
        return Verdict.Continue;
    }

    private async Task<(double V, bool Best)> SamplePointAsync(Plane plane, int ia, int ib, CancellationToken ct)
    {
        double v = await MeasureAsync(ct).ConfigureAwait(false);
        _points++;
        plane.Sampled++;
        bool best = plane.BestPose == null || Improves(v, plane.BestV);
        if (best)
        {
            plane.BestV = v;
            plane.BestPose = await _m.PoseAsync().ConfigureAwait(false);
            plane.Bia = ia;
            plane.Bib = ib;
        }
        await TraceAsync(v, ia, ib, best ? "plane-best" : "").ConfigureAwait(false);
        return (v, best);
    }

    /// <summary>
    /// One bidirectional line through the plane's best, along A or B: outward +1 then −1 from the
    /// line origin, each direction stopping at the grid edge or once past the peak. Both directions
    /// are judged against the value AT the origin, never a best the other direction just raised.
    /// </summary>
    private async Task ScanLineAsync(Plane plane, bool alongA, CancellationToken ct)
    {
        string axis = alongA ? _a : _b;
        int fixedCell = alongA ? plane.Bib : plane.Bia;
        int origin = alongA ? plane.Bia : plane.Bib;
        var originPose = plane.BestPose!;
        double originV = plane.BestV;
        int n = _p.XySteps;

        foreach (int sign in new[] { 1, -1 })
        {
            await GoToAsync(originPose, ct).ConfigureAwait(false);
            double lineBest = originV;
            int drops = 0;
            for (int k = 1; k <= 2 * n; k++)
            {
                int cell = origin + sign * k;
                if (Math.Abs(cell) > n) break;
                await _m.MoveRelativeAsync(axis, sign * _p.XyStepMm, ct).ConfigureAwait(false);
                var (v, _) = await SamplePointAsync(plane, alongA ? cell : fixedCell, alongA ? fixedCell : cell, ct)
                    .ConfigureAwait(false);
                if (Improves(v, lineBest))
                {
                    lineBest = v;
                    drops = 0;
                }
                else if (++drops >= _p.PeakDrop)
                {
                    break;   // past the peak this way
                }
            }
        }
        await GoToAsync(plane.BestPose!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Square spiral outward from the centre over the (2N+1)² grid, one cell per step. With
    /// <c>RingsStop</c> > 0 it stops expanding once that many rings in a row fail to improve.
    /// </summary>
    private async Task ScanSpiralAsync(Plane plane, CancellationToken ct)
    {
        int n = _p.XySteps;
        int total = (2 * n + 1) * (2 * n + 1);
        int ia = 0, ib = 0, legLen = 1, dir = 0, legsAtLen = 0, visited = 1;
        int ring = 0, ringsNoImp = 0;
        bool ringImproved = true;

        while (visited < total)   // bounded: visited rises by one per iteration
        {
            bool alongA = dir is 0 or 2;
            bool positive = dir is 0 or 1;
            (ia, ib) = dir switch { 0 => (ia + 1, ib), 1 => (ia, ib + 1), 2 => (ia - 1, ib), _ => (ia, ib - 1) };
            await _m.MoveRelativeAsync(alongA ? _a : _b, (positive ? 1 : -1) * _p.XyStepMm, ct).ConfigureAwait(false);
            if (--legLen == 0)
            {
                dir = (dir + 1) % 4;
                legsAtLen++;
                legLen = legsAtLen / 2 + 1;
            }

            int cellRing = Math.Max(Math.Abs(ia), Math.Abs(ib));
            if (cellRing != ring)
            {
                if (_p.RingsStop > 0)
                {
                    ringsNoImp = ringImproved ? 0 : ringsNoImp + 1;
                    if (ringsNoImp >= _p.RingsStop)
                    {
                        _log(LogLevel.Info,
                            $"  plane {_planeIdx}: past the peak at ring {ring} ({ringsNoImp} rings without improvement) — {visited}/{total} points");
                        return;
                    }
                }
                ring = cellRing;
                ringImproved = false;
            }

            var (_, best) = await SamplePointAsync(plane, ia, ib, ct).ConfigureAwait(false);
            if (best) ringImproved = true;
            visited++;
        }
    }

    /// <summary>True when <paramref name="cand"/> beats <paramref name="reference"/> by more than the noise margin (scaled by |ref| — the signal is signed).</summary>
    private bool Improves(double cand, double reference)
        => cand > reference + Math.Abs(reference) * _p.ImprovePct / 100.0;

    /// <summary>Moves to a remembered pose: a Z retreat goes first, a Z advance last.</summary>
    private async Task GoToAsync(MotionPosition target, CancellationToken ct)
    {
        var cur = await _m.PoseAsync().ConfigureAwait(false);
        double dz = ScanAxisMotion.Component(target, _z) - ScanAxisMotion.Component(cur, _z);
        bool retreat = _p.ZDir * dz < 0;
        if (retreat) await AxisToAsync(_z, target, cur, ct).ConfigureAwait(false);
        await AxisToAsync(_a, target, cur, ct).ConfigureAwait(false);
        await AxisToAsync(_b, target, cur, ct).ConfigureAwait(false);
        if (!retreat) await AxisToAsync(_z, target, cur, ct).ConfigureAwait(false);
    }

    private Task AxisToAsync(string axis, MotionPosition target, MotionPosition cur, CancellationToken ct)
    {
        double to = ScanAxisMotion.Component(target, axis);
        return Math.Abs(to - ScanAxisMotion.Component(cur, axis)) < SameMm
            ? Task.CompletedTask
            : _m.MoveAxisToAsync(axis, to, ct);
    }

    private async Task<double> MeasureAsync(CancellationToken ct)
    {
        var (v, timedOut) = await FreshSampler.ReadAsync(_fb, _p.FreshSamples, _p.SettleMinMs, _p.SettleCeilingMs, ct)
            .ConfigureAwait(false);
        if (timedOut && !_settleWarned)
        {
            _settleWarned = true;
            _log(LogLevel.Warning,
                $"Climb measure: fewer than {_p.FreshSamples} fresh samples on {_fb.Description} within {_p.SettleCeilingMs} ms — using latest");
        }
        return v;
    }

    /// <summary>Fail-safe park on the run's best point, on a fresh bounded token. Never throws.</summary>
    private async Task ParkAsync(string reason)
    {
        if (_best.Pose == null) return;
        try
        {
            using var cts = new CancellationTokenSource(FailSafeTimeoutMs);
            await GoToAsync(_best.Pose, cts.Token).ConfigureAwait(false);
            _log(LogLevel.Warning, $"Climb scan {reason}: {_m.Device} returned to the best point ({_best.V:G4} {_fb.Unit}).");
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, $"Climb scan {reason}: return to the best point FAILED — {ex.Message}");
        }
    }

    private async Task TraceAsync(double v, int ia, int ib, string note)
    {
        if (!_p.Trace) return;
        _traceSeq++;
        var p = await _m.PoseAsync().ConfigureAwait(false);
        _log(LogLevel.Info, string.Create(CultureInfo.InvariantCulture,
            $"clmb,{_traceSeq},{(Environment.TickCount64 - _t0) / 1000.0:F2},{_phase},{_planeIdx},{Um(_zTravelMm):+0.00;-0.00},{ia},{ib}," +
            $"{_a}={ScanAxisMotion.Component(p, _a):F5},{_b}={ScanAxisMotion.Component(p, _b):F5},{_z}={ScanAxisMotion.Component(p, _z):F5}," +
            $"v={v:G6},dv={(_traceLastV is { } lv ? v - lv : 0):+0.####;-0.####;0},{note}"));
        _traceLastV = v;
    }

    private ClimbScanResult Result(bool ok, string message, ClimbStop stop, double baseline, double final)
    {
        _log(ok ? LogLevel.Info : LogLevel.Warning, message);
        return new ClimbScanResult
        {
            Success = ok,
            Message = message,
            Stop = stop,
            Baseline = baseline,
            Best = double.IsFinite(_best.V) ? _best.V : baseline,
            Final = double.IsFinite(final) ? final : baseline,
            Target = _p.Target,
            ZTravelUm = _zTravelMm * 1000,
            BestZUm = _best.ZMm * 1000,
            BestAUm = _best.AMm * 1000,
            BestBUm = _best.BMm * 1000,
            PlanesSearched = _planesRastered,
            PlanesSkipped = _planesSkipped,
            Points = _points,
        };
    }

    private static double Um(double mm) => mm * 1000.0;
}
