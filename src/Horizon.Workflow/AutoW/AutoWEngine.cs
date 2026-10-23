using Horizon.Engine;
using Horizon.Workflow.HillClimb;

namespace Horizon.Workflow.AutoW;

/// <summary>
/// <b>DEPRECATED — do not use or build on this.</b> Its Run-page panel was removed; superseded by the FAU_PowerClimb .moon script (Scanning panel presets).
///
/// "Auto W maximizer" — a 1-D line search over the hexapod <b>W</b> (rotation) axis that
/// maximises the SiPhOG Sagnac signal. Rotating W laterally de-aligns the FAU, so a raw
/// reading right after a W move under-reports that angle. The engine therefore scores each
/// W candidate by <b>re-peaking XY first</b> (a small first-decline hill-climb, the same rule
/// the <see cref="HillClimbEngine"/> uses laterally) and taking that peaked value as the
/// score <c>T</c>. <c>T = f(W)</c> with XY always re-optimised.
///
/// Sequence (mirrors the operator spec):
///   0. Baseline: read signal + full pose; re-peak XY → <c>T0</c> (global best).
///   1. Probe: W += step, re-peak XY → <c>T1</c>. If <c>T1</c> beats <c>T0</c> by the
///      noise-aware threshold, climb in +W; otherwise reverse (W −= 2·step, net −step) and
///      climb in −W.
///   2. Keep stepping W in the chosen direction, re-peaking XY each time (<c>T2, T3…</c>),
///      until a step fails to improve beyond threshold (peak passed) or the ±excursion
///      budget is hit.
///   3. Return to the global-best pose (best W + its XY).
///
/// Reuses the Layer-3 <see cref="IHillClimbFeedback"/> seam (the same SiPhOG <c>SAG PWR (V)</c>
/// the Hill Climber maximises). Pure code, no UI coupling; cancelling the token halts the
/// search and still parks the hexapod at the best pose found (fail-safe, Power of 10 Rule 7).
/// </summary>
public sealed class AutoWEngine
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;

    private AutoWState _state = AutoWState.Idle;
    private readonly object _stateLock = new();

    // Per-run context
    private string _device = string.Empty;
    private IReadOnlyList<string> _axes = Array.Empty<string>();
    private IHillClimbFeedback _feedback = null!;
    private AutoWConfig _config = AutoWConfig.Default;

    // Live state during a run
    private double _baselineSigma;
    private double _effectiveThreshold;
    private double _lastV;
    private double _wStartDeg;
    private double _wCumulativeDeg;     // signed W offset from the start angle
    private double _tPeakGlobal;
    private double[]? _posPeakGlobal;
    private int _measIndex;

    public event EventHandler<AutoWStateChangedEventArgs>? StateChanged;
    public event EventHandler<AutoWBaselineRecordedEventArgs>? BaselineRecorded;
    public event EventHandler<AutoWMeasuredEventArgs>? Measured;
    public event EventHandler<AutoWResult>? Completed;
    public event EventHandler<Exception>? ErrorOccurred;

    public AutoWEngine(IHorizonEngine engine, Action<LogLevel, string>? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? ((_, _) => { });
    }

    public AutoWState CurrentState
    {
        get { lock (_stateLock) return _state; }
    }

    public bool IsRunning
    {
        get
        {
            var s = CurrentState;
            return s is AutoWState.RecordingBaseline
                or AutoWState.PeakingXY
                or AutoWState.ProbingW
                or AutoWState.ClimbingW
                or AutoWState.MovingToPeak;
        }
    }

    /// <summary>
    /// Runs the W maximiser on <paramref name="hexapodDevice"/>, maximising
    /// <paramref name="feedback"/>. Cancelling <paramref name="ct"/> halts the search; the
    /// hexapod still returns to the best pose found.
    /// </summary>
    public Task<AutoWResult> RunAsync(
        string hexapodDevice,
        IHillClimbFeedback feedback,
        AutoWConfig config,
        CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Auto W is already running.");
        if (string.IsNullOrWhiteSpace(hexapodDevice)) throw new ArgumentException("Device required.", nameof(hexapodDevice));
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _config = (config ?? throw new ArgumentNullException(nameof(config))).Clone();
        _config.Validate();
        _device = hexapodDevice;

        return Task.Run(() => RunInternalAsync(ct), ct);
    }

    private async Task<AutoWResult> RunInternalAsync(CancellationToken ct)
    {
        var result = new AutoWResult { StartedAt = DateTime.Now };
        _measIndex = 0;
        _tPeakGlobal = double.NegativeInfinity;
        _posPeakGlobal = null;
        _wCumulativeDeg = 0;

        try
        {
            var deviceInfo = _engine.Motion.GetDevice(_device)
                ?? throw new InvalidOperationException($"Motion device '{_device}' not found.");
            _axes = deviceInfo.Axes;
            if (_config.WAxisIndex >= _axes.Count || _config.XAxisIndex >= _axes.Count || _config.YAxisIndex >= _axes.Count)
                throw new InvalidOperationException(
                    $"Hexapod '{_device}' has {_axes.Count} axes; configured axis indices exceed that.");
            if (!_engine.Motion.IsConnected(_device))
                throw new InvalidOperationException($"Hexapod '{_device}' is not connected.");

            // Drive the whole search at the configured slow system velocity (W follows it on PI).
            var vel = await WithTimeoutAsync(
                _ => _engine.Motion.SetDeviceVelocityAsync(_device, _config.SystemVelocity_mmPerS),
                "Set system velocity", ct).ConfigureAwait(false);
            if (!vel.IsSuccess)
                _log(LogLevel.Warning, $"Auto W: could not set system velocity ({vel.Message}); continuing at current speed.");

            // ---- Baseline ----
            SetState(AutoWState.RecordingBaseline);
            await RecordBaselineAsync(ct).ConfigureAwait(false);
            result.BaselineSigma = _baselineSigma;
            result.EffectiveThreshold = _effectiveThreshold;

            var startPos = await ReadPositionAsync(ct).ConfigureAwait(false);
            _wStartDeg = startPos[_config.WAxisIndex];
            result.WStartDeg = _wStartDeg;

            // ---- T0: re-peak XY at the current W ----
            SetState(AutoWState.PeakingXY);
            var (t0, pos0) = await PeakXYAsync(ct).ConfigureAwait(false);
            UpdateGlobalBest(t0, pos0);
            result.T0 = t0;
            BaselineRecorded?.Invoke(this, new AutoWBaselineRecordedEventArgs(t0, _baselineSigma, _effectiveThreshold, _wStartDeg));
            RaiseMeasured(AutoWPhase.Baseline, t0, true);
            _log(LogLevel.Info, $"Auto W baseline: T0={t0:F6} at W={_wStartDeg:F4}°, effThreshold={_effectiveThreshold:P2}.");

            // ---- Probe W direction ----
            SetState(AutoWState.ProbingW);
            int direction;
            double tPrev;

            await MoveWAsync(+_config.WStep_deg, ct).ConfigureAwait(false);
            _wCumulativeDeg += _config.WStep_deg;
            var (t1, pos1) = await PeakXYAsync(ct).ConfigureAwait(false);
            bool g1 = UpdateGlobalBest(t1, pos1);
            RaiseMeasured(AutoWPhase.ProbeForward, t1, g1);

            if (t1 > t0 * (1 + _effectiveThreshold))
            {
                direction = +1;
                tPrev = t1;
                _log(LogLevel.Info, $"Auto W: +W improves (T {t0:F6} → {t1:F6}); climbing +W.");
            }
            else
            {
                // Wrong way — reverse past the start by one step (net −step from baseline).
                await MoveWAsync(-2.0 * _config.WStep_deg, ct).ConfigureAwait(false);
                _wCumulativeDeg -= 2.0 * _config.WStep_deg;
                var (t1r, pos1r) = await PeakXYAsync(ct).ConfigureAwait(false);
                bool g1r = UpdateGlobalBest(t1r, pos1r);
                RaiseMeasured(AutoWPhase.ProbeReverse, t1r, g1r);
                direction = -1;
                tPrev = t1r;
                _log(LogLevel.Info, $"Auto W: +W declined; reversing. −W gives T={t1r:F6}; climbing −W.");
            }

            // ---- Climb W in the chosen direction ----
            SetState(AutoWState.ClimbingW);
            AutoWStopReason stopReason = AutoWStopReason.PeakFound;

            // Static upper bound on iterations (Power of 10 Rule 2): the ±excursion budget /
            // step size, plus slack. The budget check below is the real terminator.
            int maxSteps = (int)Math.Ceiling(2.0 * _config.MaxWExcursion_deg / _config.WStep_deg) + 2;
            for (int i = 0; i < maxSteps; i++)
            {
                ct.ThrowIfCancellationRequested();

                double nextOffset = _wCumulativeDeg + direction * _config.WStep_deg;
                if (Math.Abs(nextOffset) > _config.MaxWExcursion_deg + 1e-9)
                {
                    stopReason = AutoWStopReason.WBudgetReached;
                    _log(LogLevel.Warning,
                        $"Auto W: W excursion budget (±{_config.MaxWExcursion_deg:F2}°) reached before a peak; returning to best.");
                    break;
                }

                await MoveWAsync(direction * _config.WStep_deg, ct).ConfigureAwait(false);
                _wCumulativeDeg = nextOffset;
                var (tn, posn) = await PeakXYAsync(ct).ConfigureAwait(false);
                bool gn = UpdateGlobalBest(tn, posn);
                RaiseMeasured(AutoWPhase.Climb, tn, gn);

                // T(N) ≤ T(N-1) within noise → we just stepped off the crest; stop.
                if (tn <= tPrev * (1 + _effectiveThreshold))
                {
                    stopReason = AutoWStopReason.PeakFound;
                    break;
                }
                tPrev = tn;
            }

            // ---- Return to the global best pose (best W + its XY) ----
            SetState(AutoWState.MovingToPeak);
            await MoveToAbsoluteAsync(_posPeakGlobal!, ct).ConfigureAwait(false);
            await SettleAsync(ct).ConfigureAwait(false);

            FillResult(result, stopReason);
            result.Message = $"Stopped: {stopReason}. Peak T={_tPeakGlobal:F6} at W offset {result.WPeakOffsetDeg:+0.###;-0.###}° "
                + $"(gain x{(t0 > 0 ? _tPeakGlobal / t0 : 0):F2}).";

            SetState(AutoWState.Completed);
            Completed?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            FillResult(result, AutoWStopReason.Cancelled);
            result.Message = "Cancelled.";

            // Best-effort return to the best pose even on cancel (fresh token).
            try
            {
                if (_posPeakGlobal != null && _engine.Motion.IsConnected(_device))
                {
                    SetState(AutoWState.MovingToPeak);
                    await MoveToAbsoluteAsync(_posPeakGlobal, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log(LogLevel.Warning, $"Auto W: failed to return to peak after cancel: {ex.Message}");
            }

            SetState(AutoWState.Cancelled);
            Completed?.Invoke(this, result);
            return result;
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, $"Auto W error: {ex.Message}");
            FillResult(result, AutoWStopReason.Error);
            result.Message = ex.Message;
            SetState(AutoWState.Error);
            ErrorOccurred?.Invoke(this, ex);
            Completed?.Invoke(this, result);
            return result;
        }
    }

    private void FillResult(AutoWResult result, AutoWStopReason reason)
    {
        result.StopReason = reason;
        result.TPeakGlobal = double.IsNegativeInfinity(_tPeakGlobal) ? 0 : _tPeakGlobal;
        result.PosPeakGlobal = _posPeakGlobal != null ? ToPosition(_posPeakGlobal) : null;
        result.WPeakOffsetDeg = _posPeakGlobal != null ? _posPeakGlobal[_config.WAxisIndex] - _wStartDeg : 0;
        result.MeasurementsTaken = _measIndex;
        result.FinishedAt = DateTime.Now;
    }

    /// <summary>
    /// Re-peaks XY at the current W using the first-decline rule on X then Y, leaving the
    /// stage at the local XY maximum. Returns that peaked score and the full pose there.
    /// </summary>
    private async Task<(double t, double[] pos)> PeakXYAsync(CancellationToken ct)
    {
        await SettleAsync(ct).ConfigureAwait(false);
        _lastV = await SampleAsync(ct).ConfigureAwait(false);   // reference after the W move
        await ScanAxisAsync(_config.XAxisIndex, ct).ConfigureAwait(false);
        await ScanAxisAsync(_config.YAxisIndex, ct).ConfigureAwait(false);
        double[] pos = await ReadPositionAsync(ct).ConfigureAwait(false);
        double t = await SampleAsync(ct).ConfigureAwait(false);
        return (t, pos);
    }

    /// <summary>
    /// First-decline scan on one lateral axis: try +; if the first step improves, keep
    /// stepping until the first non-improvement (back one); if the first step fails, restore
    /// and try −. Bounded by <see cref="AutoWConfig.MaxLateralStepsPerDirection"/>
    /// (Power of 10 Rule 2). Mirrors <c>HillClimbEngine.LateralScanAsync</c>.
    /// </summary>
    private async Task ScanAxisAsync(int axisIndex, CancellationToken ct)
    {
        double stepMm = _config.XYStep_um / 1000.0;

        foreach (int dir in new[] { +1, -1 })
        {
            ct.ThrowIfCancellationRequested();
            double vRef = _lastV;

            await MoveAxisMmAsync(axisIndex, dir * stepMm, ct).ConfigureAwait(false);
            await SettleAsync(ct).ConfigureAwait(false);
            double vAfter = await SampleAsync(ct).ConfigureAwait(false);

            if (vAfter <= vRef * (1 + _effectiveThreshold))
            {
                // First step did not improve — restore and try the other direction.
                await MoveAxisMmAsync(axisIndex, -dir * stepMm, ct).ConfigureAwait(false);
                await SettleAsync(ct).ConfigureAwait(false);
                _lastV = vRef;
                continue;
            }

            // First step worked — keep stepping until the first decline, then back one.
            double vPrev = vAfter;
            for (int extra = 0; extra < _config.MaxLateralStepsPerDirection; extra++)
            {
                ct.ThrowIfCancellationRequested();
                await MoveAxisMmAsync(axisIndex, dir * stepMm, ct).ConfigureAwait(false);
                await SettleAsync(ct).ConfigureAwait(false);
                double vNext = await SampleAsync(ct).ConfigureAwait(false);

                if (vNext > vPrev * (1 + _effectiveThreshold))
                {
                    vPrev = vNext;
                }
                else
                {
                    await MoveAxisMmAsync(axisIndex, -dir * stepMm, ct).ConfigureAwait(false);
                    await SettleAsync(ct).ConfigureAwait(false);
                    _lastV = vPrev;
                    return;
                }
            }

            _log(LogLevel.Warning,
                $"Auto W: inner XY scan on axis {axisIndex} reached step cap " +
                $"({_config.MaxLateralStepsPerDirection}) while still improving.");
            return;
        }
    }

    private bool UpdateGlobalBest(double t, double[] pos)
    {
        if (_posPeakGlobal == null || t > _tPeakGlobal * (1 + _effectiveThreshold))
        {
            _tPeakGlobal = t;
            _posPeakGlobal = (double[])pos.Clone();
            return true;
        }
        return false;
    }

    private async Task RecordBaselineAsync(CancellationToken ct)
    {
        int n = _config.BaselineSamples;
        double sum = 0;
        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            double v = ReadFeedbackChecked();
            sum += v;
            sumSq += v * v;
            if (i < n - 1) await Task.Delay(_config.BaselineSampleDelayMs, ct).ConfigureAwait(false);
        }

        double mean = sum / n;
        double variance = (sumSq / n) - (mean * mean);
        if (variance < 0) variance = 0;
        double sigma = Math.Sqrt(variance);

        _baselineSigma = sigma;
        double noiseRel = mean > 1e-9 ? (_config.NoiseSafetyMultiplier * sigma) / mean : _config.ImprovementThreshold;
        _effectiveThreshold = Math.Max(_config.ImprovementThreshold, noiseRel);
        _lastV = mean;
    }

    private async Task<double> SampleAsync(CancellationToken ct)
    {
        int n = _config.SamplesPerStep;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            sum += ReadFeedbackChecked();
            if (i < n - 1) await Task.Delay(_config.SampleDelayMs, ct).ConfigureAwait(false);
        }
        double mean = sum / n;
        _lastV = mean;
        return mean;
    }

    /// <summary>
    /// Reads the feedback and validates it is finite (Power of 10 Rule 7 — validate inputs at
    /// the boundary). A NaN/∞ would poison the peak comparisons (∞ would falsely win and park
    /// the stage there), so we fail fast; the run handler returns the stage to the last peak.
    /// </summary>
    private double ReadFeedbackChecked()
    {
        double v = _feedback.Read();
        if (!double.IsFinite(v))
            throw new InvalidOperationException(
                $"Feedback '{_feedback.Description}' returned a non-finite value ({v}).");
        return v;
    }

    /// <summary>Relative XY move in mm (X/Y are linear axes).</summary>
    private async Task MoveAxisMmAsync(int axisIndex, double deltaMm, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string axis = _axes[axisIndex];
        string what = $"Move {axis} {deltaMm * 1000.0:+0.0;-0.0} µm";
        var move = await WithTimeoutAsync(
            token => _engine.Motion.MoveRelativeAsync(_device, axis, deltaMm, token), what, ct).ConfigureAwait(false);
        if (!move.IsSuccess)
            throw new InvalidOperationException($"{what} failed: {move.Message}");
    }

    /// <summary>Relative W rotation in degrees (W is a rotary axis — no mm conversion).</summary>
    private async Task MoveWAsync(double deltaDeg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string axis = _axes[_config.WAxisIndex];
        string what = $"Rotate {axis} {deltaDeg:+0.###;-0.###}°";
        var move = await WithTimeoutAsync(
            token => _engine.Motion.MoveRelativeAsync(_device, axis, deltaDeg, token), what, ct).ConfigureAwait(false);
        if (!move.IsSuccess)
            throw new InvalidOperationException($"{what} failed: {move.Message}");
    }

    private async Task MoveToAbsoluteAsync(double[] pos, CancellationToken ct)
    {
        var move = await WithTimeoutAsync(
            token => _engine.Motion.MoveToPositionAsync(_device, ToPosition(pos), token), "Move to peak", ct)
            .ConfigureAwait(false);
        if (!move.IsSuccess)
            throw new InvalidOperationException($"Move to peak failed: {move.Message}");
    }

    private async Task<double[]> ReadPositionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pos = await WithTimeoutAsync(_ => _engine.Motion.GetPositionAsync(_device), "Read position", ct)
            .ConfigureAwait(false);
        if (!pos.IsSuccess)
            throw new InvalidOperationException($"Read position failed: {pos.Message}");
        var c = pos.Value.Components;
        return new[] { c[0], c[1], c[2], c[3], c[4], c[5] };
    }

    /// <summary>
    /// Runs a hardware operation under a per-op time bound (<see cref="AutoWConfig.IoTimeoutMs"/>).
    /// Power of 10 Rule 2 in the time domain: a hung controller or feedback link can't stall the
    /// search forever. A timeout surfaces as <see cref="TimeoutException"/> (caught by the run
    /// handler → returns to the last peak); a caller cancel still propagates as cancellation.
    /// </summary>
    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> op, string what, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = op(linked.Token);
        var timeout = Task.Delay(_config.IoTimeoutMs, linked.Token);
        var winner = await Task.WhenAny(task, timeout).ConfigureAwait(false);
        if (winner == task)
        {
            linked.Cancel();
            return await task.ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        linked.Cancel();
        throw new TimeoutException($"{what} did not complete within {_config.IoTimeoutMs} ms.");
    }

    private static MotionPosition ToPosition(double[] p)
        => new(p[0], p[1], p[2], p[3], p[4], p[5]);

    private async Task SettleAsync(CancellationToken ct)
    {
        if (_config.SettleMs > 0)
            await Task.Delay(_config.SettleMs, ct).ConfigureAwait(false);
    }

    private void SetState(AutoWState newState)
    {
        AutoWState old;
        lock (_stateLock)
        {
            old = _state;
            if (old == newState) return;
            _state = newState;
        }
        StateChanged?.Invoke(this, new AutoWStateChangedEventArgs(old, newState));
        _log(LogLevel.Debug, $"Auto W state: {old} -> {newState}");
    }

    private void RaiseMeasured(AutoWPhase phase, double t, bool improvedGlobal)
    {
        Measured?.Invoke(this, new AutoWMeasuredEventArgs(
            _measIndex++, phase, _wCumulativeDeg, t, _tPeakGlobal, improvedGlobal));
    }
}
