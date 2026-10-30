using System.Globalization;
using System.Text;
using Horizon.Engine;
using Horizon.Workflow.Scanning;

namespace Horizon.Workflow.Xcan;

/// <summary>
/// XCAN — coordinate-descent active alignment with a coarse → fine step ladder, ported
/// from FireDog's <c>hexapod_align.lua</c> (see <c>docs/dlam-ui.md</c> §XCAN and FireDog's
/// <c>docs/xcan-scan-design.md</c>). For each step size, each axis in turn is ratchet-walked
/// in one direction, then (unless the first walked uphill) the other, and left on its best
/// point; after each axis the stage recovers to the global peak if the signal fell off it.
/// On top of the bobcat algorithm: an adaptive (signal-zoned) ladder, a stop-and-stay exit
/// threshold, a fresh-sample gate instead of a blind settle, and three cycle-time trims
/// (skip-opposite, direction memory, sample reuse).
/// <para><b>Fail-safe.</b> On a STOP, a motion failure or any other error the stage returns to
/// the global peak — the best point measured — on a fresh, bounded token (Horizon's
/// active-alignment rule; FireDog left a stopped scan where it stood). An exit-threshold hit
/// stays on the point that hit it, by design.</para>
/// <para>The caller owns the feedback source: it must already be started, and stays running
/// after the pass (the two-lens sequence runs four passes on one source).</para>
/// </summary>
public sealed class XcanEngine
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;

    public XcanEngine(IHorizonEngine engine, Action<LogLevel, string>? log = null)
    {
        _engine = engine;
        _log = log ?? ((_, _) => { });
    }

    public async Task<XcanResult> RunAsync(
        string device, IScanFeedbackSource feedback, XcanParameters parameters, CancellationToken ct)
    {
        var valid = parameters.Validate();
        if (!valid.IsSuccess)
            return new XcanResult { Message = valid.Message };
        if (_engine.Motion.GetDevice(device) == null)
            return new XcanResult { Message = $"XCAN: motion device '{device}' not found." };
        if (!_engine.Motion.IsConnected(device))
            return new XcanResult { Message = $"XCAN: motion device '{device}' is not connected." };

        var run = new XcanRun(_engine.Motion, device, feedback, parameters, _log);
        return await run.ExecuteAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>The state of one XCAN pass — the Lua script's locals, one instance per run.</summary>
internal sealed class XcanRun
{
    private const int FailSafeTimeoutMs = 30_000;
    private const int BaselineSpacingMs = 50;
    private const int AxisCheckDelayMs = 100;

    private readonly IMotionFacet _motion;
    private readonly ScanAxisMotion _m;
    private readonly IScanFeedbackSource _fb;
    private readonly XcanParameters _p;
    private readonly Action<LogLevel, string> _log;

    private bool _exitHit;
    private bool _settleWarned;
    private int _samples;
    private double _peakV = double.NegativeInfinity;
    private MotionPosition? _peakPose;
    private readonly Dictionary<string, int> _dirMem = new(StringComparer.OrdinalIgnoreCase);

    // Trace context (FireDog's TC / T tables).
    private string _phase = "base";
    private string _axis = "-";
    private int _dir;
    private double _step;
    private int _traceSeq;
    private double? _traceLastV;
    private MotionPosition? _traceLastPose;
    private readonly long _t0 = Environment.TickCount64;

    public XcanRun(IMotionFacet motion, string device, IScanFeedbackSource feedback,
                   XcanParameters parameters, Action<LogLevel, string> log)
    {
        _m = new ScanAxisMotion(motion, device, parameters.MoveTimeoutMs);
        _fb = feedback;
        _p = parameters;
        _log = log;
        _motion = motion;
    }

    public async Task<XcanResult> ExecuteAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        int passesRun = 0, passesSkipped = 0;
        double baseline = 0;
        _log(LogLevel.Info,
            $"XCAN: {_m.Device} on {_fb.Description}, axes {string.Join(",", _p.Axes)}, " +
            $"steps {Um(_p.StepsMm)} µm" + (_p.ExitThreshold != 0 ? $", exit at {_p.ExitThreshold:G4} {_fb.Unit}" : ""));
        try
        {
            if (_p.VelocityMmPerSec > 0)
            {
                var v = await _motion.SetDeviceVelocityAsync(_m.Device, _p.VelocityMmPerSec).ConfigureAwait(false);
                if (!v.IsSuccess)
                    return Done(started, false, $"XCAN: set scan velocity failed — {v.Message}", baseline, baseline, 0, 0);
            }
            if (_p.Adaptive) LogZones();
            if (_p.Trace)
                _log(LogLevel.Info, $"xcan,header: seq,t_s,phase,axis,dir,step_um,pos({string.Join(",", _p.Axes)}),value,dv,dmove_um,note");

            // Averaged baseline (bobcat averages ONLY the baseline).
            double? carry = null;
            double sum = 0;
            for (int i = 0; i < _p.AvgCount; i++)
            {
                double v = await MeasureAsync(ct).ConfigureAwait(false);
                await TraceAsync(v, "baseline").ConfigureAwait(false);
                sum += v;
                carry = v;
                if (i < _p.AvgCount - 1) await Task.Delay(BaselineSpacingMs, ct).ConfigureAwait(false);
            }
            baseline = sum / _p.AvgCount;
            _log(LogLevel.Info, $"XCAN baseline = {baseline:G4} {_fb.Unit}");
            if (Math.Abs(baseline) < _p.MinSignal)
                return Done(started, false,
                    $"XCAN: baseline {baseline:G3} below min signal {_p.MinSignal:G3} — is the beam on?", baseline, baseline, 0, 0);
            await UpdatePeakAsync(baseline).ConfigureAwait(false);

            foreach (var step in _p.StepsMm)
            {
                if (_p.Adaptive)
                {
                    // The best REACHABLE signal, never one raw sample: a noisy read must not
                    // bounce the ladder back to coarse.
                    double reference = Math.Max(carry ?? 0, _peakV);
                    double zoneStep = ZoneStep(reference);
                    if (step > zoneStep * 1.001)
                    {
                        _log(LogLevel.Info,
                            $"XCAN pass @ {step * 1000:0.###} µm skipped (adaptive: signal {reference:G4} → zone step {zoneStep * 1000:0.###} µm)");
                        passesSkipped++;
                        continue;
                    }
                }
                passesRun++;
                _log(LogLevel.Info, $"XCAN pass @ {step * 1000:0.###} µm");
                carry = await PassAsync(step, carry, ct).ConfigureAwait(false);
                if (_exitHit) break;
            }

            // Finish: on the threshold point when the exit fired, else on the best point seen.
            SetPhase("final", "-", 0, 0);
            double finalV = await MeasureAsync(ct).ConfigureAwait(false);
            await TraceAsync(finalV, "final").ConfigureAwait(false);
            if (!_exitHit && _peakPose != null && _peakV > finalV)
            {
                await _m.MoveToAsync(_peakPose, ct).ConfigureAwait(false);
                finalV = await MeasureAsync(ct).ConfigureAwait(false);
                await TraceAsync(finalV, "final@gpeak").ConfigureAwait(false);
            }

            var gain = baseline > 0 && finalV > 0 ? $"{finalV / baseline:0.00}x" : "n/a (signal not positive)";
            return Done(started, true,
                $"XCAN done: baseline {baseline:G4} → best {finalV:G4} {_fb.Unit} (gain {gain})" +
                (_exitHit ? ", exit threshold hit" : ""), baseline, finalV, passesRun, passesSkipped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ParkAsync("stopped").ConfigureAwait(false);
            return Done(started, false, "XCAN stopped — returned to the best point measured.",
                baseline, _peakV, passesRun, passesSkipped) with { Halted = true };
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, $"XCAN error: {ex.Message}");
            await ParkAsync("error").ConfigureAwait(false);
            return Done(started, false, $"XCAN failed: {ex.Message}", baseline, _peakV, passesRun, passesSkipped);
        }
    }

    /// <summary>One rung of the ladder over every axis; returns the reading at the point the stage is left on.</summary>
    private async Task<double?> PassAsync(double step, double? carry, CancellationToken ct)
    {
        foreach (var axis in _p.Axes)
        {
            var (_, curV) = await ScanAxisAsync(axis, step, carry, ct).ConfigureAwait(false);
            if (_exitHit) return carry;

            // Global-peak recovery — the return-to-best sample doubles as the check (reuse).
            SetPhase("check", axis, 0, step);
            double? cur = _p.ReuseSample ? curV : null;
            if (cur == null)
            {
                await Task.Delay(AxisCheckDelayMs, ct).ConfigureAwait(false);
                cur = await MeasureAsync(ct).ConfigureAwait(false);
                await TraceAsync(cur.Value, "axis-done").ConfigureAwait(false);
                if (_exitHit) return cur;
            }
            carry = cur;
            if (cur > 0 && _peakPose != null && (_peakV - cur.Value) / cur.Value > _p.ImprovePct / 100.0)
            {
                _log(LogLevel.Info, $"  recovering to global peak ({_peakV:G4} > {cur:G4})");
                await _m.MoveToAsync(_peakPose, ct).ConfigureAwait(false);
                double rv = await MeasureAsync(ct).ConfigureAwait(false);
                await TraceAsync(rv, "recovered").ConfigureAwait(false);
                carry = rv;
            }
        }
        return carry;
    }

    /// <summary>One axis at one step: probe one direction, the other unless the peak is bracketed, keep the better.</summary>
    private async Task<(double BestV, double? CurV)> ScanAxisAsync(string axis, double step, double? v0, CancellationToken ct)
    {
        double p0 = await _m.AxisAsync(axis).ConfigureAwait(false);
        int d1 = _p.DirMemory && _dirMem.TryGetValue(axis, out var mem) ? mem : 1;

        var first = await ScanDirectionAsync(axis, step, d1, _p.ReuseSample ? v0 : null, ct).ConfigureAwait(false);
        if (_exitHit) return (first.MaxV, null);
        if (first.Improved)
        {
            _dirMem[axis] = d1;
            if (_p.SkipOpposite)
            {
                _log(LogLevel.Info, $"  {axis} @ {step * 1000:0.###} µm: best={first.MaxV:G4} (one-sided)");
                return (first.MaxV, first.CurV);
            }
        }

        // A no-improvement walk already returned to p0, so its CurV is a fresh sample there.
        await _m.MoveAxisToAsync(axis, p0, ct).ConfigureAwait(false);
        var second = await ScanDirectionAsync(axis, step, -d1,
            _p.ReuseSample && !first.Improved ? first.CurV : null, ct).ConfigureAwait(false);
        if (_exitHit) return (second.MaxV, null);
        if (second.Improved) _dirMem[axis] = -d1;

        var (bestV, bestPos) = second.MaxV > first.MaxV ? (second.MaxV, second.MaxPos) : (first.MaxV, first.MaxPos);
        double? curV = null;
        if (Math.Abs(bestPos - second.MaxPos) <= _p.TolMm)
            curV = second.CurV;   // already standing on the best point
        else
            await _m.MoveAxisToAsync(axis, bestPos, ct).ConfigureAwait(false);
        _log(LogLevel.Info, $"  {axis} @ {step * 1000:0.###} µm: best={bestV:G4}");
        return (bestV, curV);
    }

    private readonly record struct Walk(double MaxV, double MaxPos, bool Improved, double CurV);

    /// <summary>The ratchet walk: one axis, one direction, back to its best point unless the exit fired.</summary>
    private async Task<Walk> ScanDirectionAsync(string axis, double step, int dir, double? v0, CancellationToken ct)
    {
        SetPhase("scan", axis, dir, step);
        double maxV;
        if (v0 is { } reused)
        {
            maxV = reused;
        }
        else
        {
            maxV = await MeasureAsync(ct).ConfigureAwait(false);
            await TraceAsync(maxV, "start").ConfigureAwait(false);
        }
        var startPose = await _m.PoseAsync().ConfigureAwait(false);
        double maxPos = ScanAxisMotion.Component(startPose, axis);
        UpdatePeak(maxV, startPose);

        double prev = maxV, dist = 0;
        int consec = 0;
        bool movedFromMax = false, improved = false;
        int maxSteps = (int)Math.Ceiling(_p.MaxTravelMm / step) + 1;   // bounded walk
        for (int k = 0; k < maxSteps && !_exitHit && consec < _p.ConsecLimit && dist < _p.MaxTravelMm; k++)
        {
            await _m.MoveRelativeAsync(axis, dir * step, ct).ConfigureAwait(false);
            double v = await MeasureAsync(ct).ConfigureAwait(false);
            dist += step;
            if (v > maxV)
            {
                var pose = await _m.PoseAsync().ConfigureAwait(false);
                maxV = v;
                maxPos = ScanAxisMotion.Component(pose, axis);
                consec = 0;
                movedFromMax = false;
                improved = true;
                UpdatePeak(v, pose);
                await TraceAsync(v, "better").ConfigureAwait(false);
            }
            else
            {
                consec++;
                movedFromMax = true;
                if (prev > 0 && (prev - v) / prev > _p.SigDropPct / 100.0)
                {
                    await TraceAsync(v, "drop-break").ConfigureAwait(false);
                    break;
                }
                await TraceAsync(v, $"worse({consec}/{_p.ConsecLimit})").ConfigureAwait(false);
            }
            prev = v;
        }

        double curV = maxV;
        // On a threshold exit the stage STAYS on the point that hit it — no walk-back.
        if (movedFromMax && !_exitHit)
        {
            await _m.MoveAxisToAsync(axis, maxPos, ct).ConfigureAwait(false);
            double v = await MeasureAsync(ct).ConfigureAwait(false);
            await TraceAsync(v, "return-to-best").ConfigureAwait(false);
            if (v > maxV) maxV = v;
            curV = v;
        }
        return new Walk(maxV, maxPos, improved, curV);
    }

    // --- measurement / peak --------------------------------------------------------------

    private async Task<double> MeasureAsync(CancellationToken ct)
    {
        var (v, timedOut) = await FreshSampler.ReadAsync(_fb, _p.FreshSamples, _p.SettleMinMs, _p.SettleCeilingMs, ct)
            .ConfigureAwait(false);
        _samples++;
        if (timedOut && !_settleWarned)
        {
            _settleWarned = true;
            _log(LogLevel.Warning,
                $"XCAN measure: fewer than {_p.FreshSamples} fresh samples on {_fb.Description} within {_p.SettleCeilingMs} ms — using latest");
        }
        if (_p.ExitThreshold != 0 && v >= _p.ExitThreshold && !_exitHit)
        {
            _exitHit = true;
            _log(LogLevel.Info, $"XCAN exit threshold reached: {v:G4} >= {_p.ExitThreshold:G4}");
        }
        return v;
    }

    private void UpdatePeak(double v, MotionPosition pose)
    {
        if (v > _peakV)
        {
            _peakV = v;
            _peakPose = pose;
        }
    }

    private async Task UpdatePeakAsync(double v)
    {
        if (v > _peakV) UpdatePeak(v, await _m.PoseAsync().ConfigureAwait(false));
    }

    /// <summary>Fail-safe park on the global peak, on a fresh bounded token. Never throws.</summary>
    private async Task ParkAsync(string reason)
    {
        if (_exitHit || _peakPose == null) return;
        try
        {
            using var cts = new CancellationTokenSource(FailSafeTimeoutMs);
            await _m.MoveToAsync(_peakPose, cts.Token).ConfigureAwait(false);
            _log(LogLevel.Warning, $"XCAN {reason}: {_m.Device} returned to the global peak ({_peakV:G4} {_fb.Unit}).");
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, $"XCAN {reason}: return to the global peak FAILED — {ex.Message}");
        }
    }

    /// <summary>Adaptive zoning: signal → the zone's step. Equal log slices of [low, high]; below = coarsest, above = finest.</summary>
    private double ZoneStep(double v)
    {
        var steps = _p.StepsMm;
        if (v <= _p.ZoneLow) return steps[0];
        if (v >= _p.ZoneHigh) return steps[^1];
        double frac = Math.Log(v / _p.ZoneLow) / Math.Log(_p.ZoneHigh / _p.ZoneLow);
        int idx = Math.Min((int)Math.Floor(frac * steps.Count), steps.Count - 1);
        return steps[idx];
    }

    private void LogZones()
    {
        int n = _p.StepsMm.Count;
        var bounds = Enumerable.Range(1, Math.Max(0, n - 1))
            .Select(i => (_p.ZoneLow * Math.Pow(_p.ZoneHigh / _p.ZoneLow, (double)i / n)).ToString("G3", CultureInfo.InvariantCulture));
        _log(LogLevel.Info,
            $"XCAN adaptive ladder: {n} log zones over [{_p.ZoneLow:G3}, {_p.ZoneHigh:G3}] (bounds {string.Join("/", bounds)}), steps {Um(_p.StepsMm)} µm");
    }

    // --- trace ---------------------------------------------------------------------------

    private void SetPhase(string phase, string axis, int dir, double step)
    {
        _phase = phase;
        _axis = axis;
        _dir = dir;
        _step = step;
    }

    private async Task TraceAsync(double v, string note)
    {
        if (!_p.Trace) return;
        _traceSeq++;
        var pose = await _m.PoseAsync().ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"xcan,{_traceSeq},{(Environment.TickCount64 - _t0) / 1000.0:F2},{_phase},{_axis},{_dir:+0;-0;0},{_step * 1000:0.###}");
        foreach (var a in _p.Axes)
            sb.Append(CultureInfo.InvariantCulture, $",{a}={ScanAxisMotion.Component(pose, a):F5}");
        sb.Append(CultureInfo.InvariantCulture, $",v={v:G6},dv={(_traceLastV is { } lv ? v - lv : 0):+0.####;-0.####;0}");
        var moves = new List<string>();
        if (_traceLastPose != null)
            foreach (var a in _p.Axes)
            {
                double d = (ScanAxisMotion.Component(pose, a) - ScanAxisMotion.Component(_traceLastPose, a)) * 1000.0;
                if (Math.Abs(d) > 0.01) moves.Add(string.Create(CultureInfo.InvariantCulture, $"d{a}={d:+0.00;-0.00}"));
            }
        sb.Append(',').Append(moves.Count > 0 ? string.Join(" ", moves) : "dmove=0").Append(',').Append(note);
        _log(LogLevel.Info, sb.ToString());
        _traceLastV = v;
        _traceLastPose = pose;
    }

    private XcanResult Done(DateTime started, bool ok, string message, double baseline, double best, int run, int skipped)
    {
        _log(ok ? LogLevel.Info : LogLevel.Warning, message);
        return new XcanResult
        {
            Success = ok,
            Message = message,
            Baseline = baseline,
            Best = double.IsFinite(best) ? best : baseline,
            Peak = double.IsFinite(_peakV) ? _peakV : baseline,
            ExitHit = _exitHit,
            PassesRun = run,
            PassesSkipped = skipped,
            Samples = _samples,
            Duration = DateTime.UtcNow - started,
        };
    }

    private static string Um(IEnumerable<double> mm)
        => string.Join(",", mm.Select(s => (s * 1000).ToString("0.###", CultureInfo.InvariantCulture)));
}
