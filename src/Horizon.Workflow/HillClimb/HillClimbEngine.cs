using Horizon.Engine;

namespace Horizon.Workflow.HillClimb;

/// <summary>
/// Coordinate-descent hill climber for FAU-to-PIC active alignment, an exact port
/// of the legacy <c>acs_solution_saa</c> <c>HillClimberAlgorithm</c>. Layer 2:
/// drives a hexapod through <see cref="IHorizonEngine"/>'s motion facet and
/// maximises the signal from an injected <see cref="IHillClimbFeedback"/> (the
/// FAAM SiPhOG <c>SAG PWR (V)</c>). Pure code, no UI coupling — mirrors
/// <see cref="Scanning.HexapodScanEngine"/>; a front-end constructs it directly.
///
/// Per outer loop:
///   1. Step Z+ while signal improves; stop after <c>ZStallLimit</c> consecutive non-improvements.
///   2. Backtrack Z to the loop's best Z.
///   3. Lateral X correction: try one direction; if the first step improves, keep stepping
///      until first decline, then back one. If the first step fails, restore and try the other.
///   4. Same for Y.
/// Stop when cumulative Z+ reaches <c>ZBudget_um</c> (hard) or a whole loop makes no
/// improvement (converged). After any stop, move to the global peak position.
/// </summary>
public sealed class HillClimbEngine
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;

    private HillClimbState _state = HillClimbState.Idle;
    private readonly object _stateLock = new();

    // Per-run context
    private string _device = string.Empty;
    private IReadOnlyList<string> _axes = Array.Empty<string>();
    private IHillClimbFeedback _feedback = null!;
    private HillClimbConfig _config = HillClimbConfig.Default;

    // Live state during a run
    private double _vStart;
    private double _vPeakGlobal;
    private double[]? _posPeakGlobal;
    private double _zCumulative_um;
    private double _lastV;
    private double _effectiveThreshold;
    private double _baselineSigma;
    private double _currentZStep_um;
    private double _currentXYStep_um;

    public event EventHandler<HillClimbStateChangedEventArgs>? StateChanged;
    public event EventHandler<HillClimbBaselineRecordedEventArgs>? BaselineRecorded;
    public event EventHandler<HillClimbStepEventArgs>? StepCompleted;
    public event EventHandler<HillClimbLoopEventArgs>? LoopCompleted;
    public event EventHandler<HillClimbProgressEventArgs>? ProgressUpdated;
    public event EventHandler<HillClimbResult>? ClimbCompleted;
    public event EventHandler<Exception>? ErrorOccurred;

    public HillClimbEngine(IHorizonEngine engine, Action<LogLevel, string>? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? ((_, _) => { });
    }

    public HillClimbState CurrentState
    {
        get { lock (_stateLock) return _state; }
    }

    public bool IsRunning
    {
        get
        {
            var s = CurrentState;
            return s is HillClimbState.RecordingBaseline
                or HillClimbState.Climbing
                or HillClimbState.MovingToPeak;
        }
    }

    /// <summary>
    /// Runs the climb on <paramref name="hexapodDevice"/>, maximising
    /// <paramref name="feedback"/>. Cancelling <paramref name="ct"/> halts the climb;
    /// the hexapod still returns to the best position found (legacy contract).
    /// </summary>
    public Task<HillClimbResult> RunAsync(
        string hexapodDevice,
        IHillClimbFeedback feedback,
        HillClimbConfig config,
        CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("HillClimb is already running.");
        if (string.IsNullOrWhiteSpace(hexapodDevice)) throw new ArgumentException("Device required.", nameof(hexapodDevice));
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _config = (config ?? throw new ArgumentNullException(nameof(config))).Clone();
        _config.Validate();
        _device = hexapodDevice;

        return Task.Run(() => RunInternalAsync(ct), ct);
    }

    private async Task<HillClimbResult> RunInternalAsync(CancellationToken ct)
    {
        var result = new HillClimbResult { StartedAt = DateTime.Now };

        try
        {
            var deviceInfo = _engine.Motion.GetDevice(_device)
                ?? throw new InvalidOperationException($"Motion device '{_device}' not found.");
            _axes = deviceInfo.Axes;
            if (_config.ZAxisIndex >= _axes.Count || _config.XAxisIndex >= _axes.Count || _config.YAxisIndex >= _axes.Count)
                throw new InvalidOperationException(
                    $"Hexapod '{_device}' has {_axes.Count} axes; configured axis indices exceed that.");
            if (!_engine.Motion.IsConnected(_device))
                throw new InvalidOperationException($"Hexapod '{_device}' is not connected.");

            // ---- Baseline ----
            SetState(HillClimbState.RecordingBaseline);
            await RecordBaselineAsync(ct).ConfigureAwait(false);
            result.VStart = _vStart;
            result.BaselineSigma = _baselineSigma;
            result.EffectiveThreshold = _effectiveThreshold;

            // ---- Climb ----
            SetState(HillClimbState.Climbing);
            _vPeakGlobal = _vStart;
            _posPeakGlobal = await ReadPositionAsync(ct).ConfigureAwait(false);
            _zCumulative_um = 0;
            _lastV = _vStart;
            int loopIdx = 0;
            HillClimbStopReason stopReason = HillClimbStopReason.ZBudgetReached;

            while (_zCumulative_um < _config.ZBudget_um)
            {
                ct.ThrowIfCancellationRequested();
                loopIdx++;

                double vLoopStart = _vPeakGlobal;
                double zLoopStart = _zCumulative_um;
                var loopBest = new LoopBest
                {
                    V = _vPeakGlobal,
                    Pos = (double[])_posPeakGlobal!.Clone()
                };
                bool loopImproved = false;

                // ---- Z+ sub-loop ----
                int stall = 0;
                while (stall < _config.ZStallLimit && _zCumulative_um < _config.ZBudget_um && !TargetReached())
                {
                    ct.ThrowIfCancellationRequested();
                    _currentZStep_um = PickZStep_um(_config.ZBudget_um - _zCumulative_um);
                    _currentXYStep_um = PickXYStep_um(_config.ZBudget_um - _zCumulative_um);

                    double vBefore = _lastV;
                    await MoveAxisAsync(_config.ZAxisIndex, +_currentZStep_um / 1000.0, ct).ConfigureAwait(false);
                    await SettleAsync(ct).ConfigureAwait(false);
                    double[] curPos = await ReadPositionAsync(ct).ConfigureAwait(false);
                    double vAfter = await SampleAsync(ct).ConfigureAwait(false);
                    _zCumulative_um += _currentZStep_um;

                    bool improvedLocal = vAfter > vBefore * (1 + _effectiveThreshold);
                    bool improvedGlobal = false;

                    if (vAfter > loopBest.V * (1 + _effectiveThreshold))
                    {
                        loopBest.V = vAfter;
                        loopBest.Pos = (double[])curPos.Clone();
                        loopImproved = true;
                    }
                    if (vAfter > _vPeakGlobal * (1 + _effectiveThreshold))
                    {
                        _vPeakGlobal = vAfter;
                        _posPeakGlobal = (double[])curPos.Clone();
                        improvedGlobal = true;
                    }

                    if (improvedLocal) stall = 0;
                    else stall++;

                    RaiseStep(HillClimbPhase.ZAdvance, _config.ZAxisIndex, +1, _currentZStep_um, vBefore, vAfter, improvedGlobal);
                    RaiseProgress();
                }

                // Early exit: global peak reached the Sagnac target — skip lateral correction.
                if (TargetReached())
                {
                    stopReason = HillClimbStopReason.TargetReached;
                    break;
                }

                // ---- Z backtrack to loop's best Z ----
                if (stall >= _config.ZStallLimit)
                {
                    double[] curPos = await ReadPositionAsync(ct).ConfigureAwait(false);
                    double dzMm = loopBest.Pos[_config.ZAxisIndex] - curPos[_config.ZAxisIndex];
                    if (Math.Abs(dzMm) > 1e-6)
                    {
                        double vBefore = _lastV;
                        await MoveAxisAsync(_config.ZAxisIndex, dzMm, ct).ConfigureAwait(false);
                        await SettleAsync(ct).ConfigureAwait(false);
                        _lastV = loopBest.V;
                        RaiseStep(HillClimbPhase.ZBacktrack, _config.ZAxisIndex,
                            Math.Sign(dzMm), Math.Abs(dzMm) * 1000.0, vBefore, _lastV, false);
                    }
                }

                // ---- X correction ----
                bool xImproved = await LateralScanAsync(
                    _config.XAxisIndex, _currentXYStep_um, HillClimbPhase.XCorrection, loopBest, ct).ConfigureAwait(false);

                // ---- Y correction ----
                bool yImproved = await LateralScanAsync(
                    _config.YAxisIndex, _currentXYStep_um, HillClimbPhase.YCorrection, loopBest, ct).ConfigureAwait(false);

                loopImproved = loopImproved || xImproved || yImproved;

                RaiseLoop(loopIdx, _zCumulative_um - zLoopStart, vLoopStart, loopBest.V, loopImproved);

                if (TargetReached())
                {
                    stopReason = HillClimbStopReason.TargetReached;
                    break;
                }

                if (!loopImproved)
                {
                    stopReason = HillClimbStopReason.Converged;
                    break;
                }
            }

            if (_zCumulative_um >= _config.ZBudget_um
                && stopReason != HillClimbStopReason.Converged
                && stopReason != HillClimbStopReason.TargetReached)
                stopReason = HillClimbStopReason.ZBudgetReached;

            // ---- Return to global peak ----
            SetState(HillClimbState.MovingToPeak);
            await MoveToAbsoluteAsync(_posPeakGlobal!, ct).ConfigureAwait(false);
            await SettleAsync(ct).ConfigureAwait(false);

            result.StopReason = stopReason;
            result.VPeakGlobal = _vPeakGlobal;
            result.PosPeakGlobal = ToPosition(_posPeakGlobal!);
            result.ZTotalTraveled_um = _zCumulative_um;
            result.LoopsExecuted = loopIdx;
            result.FinishedAt = DateTime.Now;
            result.Message = $"Stopped: {stopReason}. Peak {_vPeakGlobal:F6} (gain x{(_vStart > 0 ? _vPeakGlobal / _vStart : 0):F2}).";

            SetState(HillClimbState.Completed);
            ClimbCompleted?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.StopReason = HillClimbStopReason.Cancelled;
            result.VPeakGlobal = _vPeakGlobal;
            result.PosPeakGlobal = _posPeakGlobal != null ? ToPosition(_posPeakGlobal) : null;
            result.ZTotalTraveled_um = _zCumulative_um;
            result.FinishedAt = DateTime.Now;
            result.Message = "Cancelled.";

            // Best-effort return to global peak even on cancel (fresh token).
            try
            {
                if (_posPeakGlobal != null && _engine.Motion.IsConnected(_device))
                {
                    SetState(HillClimbState.MovingToPeak);
                    await MoveToAbsoluteAsync(_posPeakGlobal, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log(LogLevel.Warning, $"HillClimb: failed to return to peak after cancel: {ex.Message}");
            }

            SetState(HillClimbState.Cancelled);
            ClimbCompleted?.Invoke(this, result);
            return result;
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, $"HillClimb error: {ex.Message}");
            result.StopReason = HillClimbStopReason.Error;
            result.Message = ex.Message;
            result.FinishedAt = DateTime.Now;
            SetState(HillClimbState.Error);
            ErrorOccurred?.Invoke(this, ex);
            ClimbCompleted?.Invoke(this, result);
            return result;
        }
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

        _vStart = mean;
        _baselineSigma = sigma;
        double noiseRel = mean > 1e-9 ? (_config.NoiseSafetyMultiplier * sigma) / mean : _config.ImprovementThreshold;
        _effectiveThreshold = Math.Max(_config.ImprovementThreshold, noiseRel);
        _lastV = mean;

        _log(LogLevel.Info, $"HillClimb baseline: V={_vStart:F6}, sigma={_baselineSigma:F6}, effThreshold={_effectiveThreshold:P2} ({_feedback.Description}).");

        BaselineRecorded?.Invoke(this, new HillClimbBaselineRecordedEventArgs(_vStart, _baselineSigma, _effectiveThreshold));
    }

    /// <summary>Per-outer-loop best, mutated in place (async methods can't take <c>ref</c>).</summary>
    private sealed class LoopBest
    {
        public double V;
        public double[] Pos = Array.Empty<double>();
    }

    /// <summary>Lateral scan on a single axis using the first-decline rule. Updates loop best and global peak in place.</summary>
    private async Task<bool> LateralScanAsync(
        int axisIndex, double stepSize_um, HillClimbPhase phase,
        LoopBest loopBest, CancellationToken ct)
    {
        bool improvedGlobalAny = false;
        double stepMm = stepSize_um / 1000.0;

        foreach (int dir in new[] { +1, -1 })
        {
            ct.ThrowIfCancellationRequested();

            double vRef = _lastV;

            // First trial step
            await MoveAxisAsync(axisIndex, dir * stepMm, ct).ConfigureAwait(false);
            await SettleAsync(ct).ConfigureAwait(false);
            double[] curPos = await ReadPositionAsync(ct).ConfigureAwait(false);
            double vAfter = await SampleAsync(ct).ConfigureAwait(false);

            bool improvedGlobalStep = TryUpdateBests(vAfter, curPos, loopBest);
            improvedGlobalAny |= improvedGlobalStep;
            RaiseStep(phase, axisIndex, dir, stepSize_um, vRef, vAfter, improvedGlobalStep);

            if (vAfter <= vRef * (1 + _effectiveThreshold))
            {
                // First step did not improve — restore and try the other direction
                double vBeforeRestore = _lastV;
                await MoveAxisAsync(axisIndex, -dir * stepMm, ct).ConfigureAwait(false);
                await SettleAsync(ct).ConfigureAwait(false);
                _lastV = vRef;
                RaiseStep(phase, axisIndex, -dir, stepSize_um, vBeforeRestore, _lastV, false);
                continue;
            }

            // First step worked — keep stepping until first non-improvement. Bounded by
            // MaxLateralStepsPerDirection (Power of 10 Rule 2: a fixed, statically-provable
            // upper bound, so a noisy signal can't drive an unbounded walk).
            double vPrev = vAfter;
            bool declined = false;
            for (int extra = 0; extra < _config.MaxLateralStepsPerDirection; extra++)
            {
                ct.ThrowIfCancellationRequested();
                double vBefore = vPrev;
                await MoveAxisAsync(axisIndex, dir * stepMm, ct).ConfigureAwait(false);
                await SettleAsync(ct).ConfigureAwait(false);
                double[] stepPos = await ReadPositionAsync(ct).ConfigureAwait(false);
                double vNext = await SampleAsync(ct).ConfigureAwait(false);

                if (vNext > vPrev * (1 + _effectiveThreshold))
                {
                    bool gImp = TryUpdateBests(vNext, stepPos, loopBest);
                    improvedGlobalAny |= gImp;
                    RaiseStep(phase, axisIndex, dir, stepSize_um, vBefore, vNext, gImp);
                    vPrev = vNext;
                }
                else
                {
                    // First decline — step back one
                    RaiseStep(phase, axisIndex, dir, stepSize_um, vBefore, vNext, false);
                    await MoveAxisAsync(axisIndex, -dir * stepMm, ct).ConfigureAwait(false);
                    await SettleAsync(ct).ConfigureAwait(false);
                    _lastV = vPrev;
                    declined = true;
                    break;
                }
            }

            if (!declined)
                _log(LogLevel.Warning,
                    $"HillClimb: lateral {phase} reached step cap ({_config.MaxLateralStepsPerDirection}) " +
                    "while still improving — stopping this direction (signal not yet peaked).");

            return improvedGlobalAny; // Done with this axis
        }

        return improvedGlobalAny;
    }

    /// <summary>
    /// True once the global peak has reached the configured Sagnac target fraction
    /// (<c>VPeak ≥ TargetSagnacV · TargetReachedFraction</c>). Disabled when
    /// <see cref="HillClimbConfig.TargetSagnacV"/> is 0.
    /// </summary>
    private bool TargetReached()
        => _config.TargetSagnacV > 0
           && _vPeakGlobal >= _config.TargetSagnacV * _config.TargetReachedFraction;

    private bool TryUpdateBests(double v, double[] curPos, LoopBest loopBest)
    {
        bool improvedGlobal = false;
        if (v > loopBest.V * (1 + _effectiveThreshold))
        {
            loopBest.V = v;
            loopBest.Pos = (double[])curPos.Clone();
        }
        if (v > _vPeakGlobal * (1 + _effectiveThreshold))
        {
            _vPeakGlobal = v;
            _posPeakGlobal = (double[])curPos.Clone();
            improvedGlobal = true;
        }
        return improvedGlobal;
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
    /// Reads the feedback and validates it is finite (Power of 10 Rule 7 — validate
    /// inputs at the boundary). A NaN/∞ from the sensor would poison the peak
    /// comparisons (∞ would falsely win and park the stage there), so we fail fast;
    /// the run's top-level handler returns the stage to the last known peak.
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
        string what = $"Move {axis} {deltaMm * 1000.0:+0.0;-0.0} µm";
        var move = await WithTimeoutAsync(
            token => _engine.Motion.MoveRelativeAsync(_device, axis, deltaMm, token), what, ct).ConfigureAwait(false);
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
        // GetPositionAsync has no token, so race it against the timeout explicitly.
        var pos = await WithTimeoutAsync(_ => _engine.Motion.GetPositionAsync(_device), "Read position", ct)
            .ConfigureAwait(false);
        if (!pos.IsSuccess)
            throw new InvalidOperationException($"Read position failed: {pos.Message}");
        var c = pos.Value.Components;
        return new[] { c[0], c[1], c[2], c[3], c[4], c[5] };
    }

    /// <summary>
    /// Runs a hardware operation under a per-op time bound (<see cref="HillClimbConfig.IoTimeoutMs"/>).
    /// Power of 10 Rule 2 in the time domain: every I/O completes within a fixed bound, so a
    /// hung controller or feedback link can't stall the climb forever. A timeout surfaces as
    /// <see cref="TimeoutException"/> (caught by the run's handler → returns to the last peak);
    /// a caller-requested cancel still propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> op, string what, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = op(linked.Token);
        // Race against a delay so the bound holds even for ops that ignore the token
        // (e.g. GetPositionAsync, which takes none).
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

    private async Task SettleAsync(CancellationToken ct)
    {
        if (_config.SettleMs > 0)
            await Task.Delay(_config.SettleMs, ct).ConfigureAwait(false);
    }

    private double PickZStep_um(double remaining_um)
    {
        if (remaining_um <= _config.FineRemainingThreshold_um) return _config.StepFine_um;
        if (remaining_um <= _config.MidRemainingThreshold_um) return _config.ZStepMid_um;
        return _config.ZStepInitial_um;
    }

    private double PickXYStep_um(double remaining_um)
    {
        if (remaining_um <= _config.FineRemainingThreshold_um) return _config.StepFine_um;
        return _config.XYStepInitial_um;
    }

    private void SetState(HillClimbState newState)
    {
        HillClimbState old;
        lock (_stateLock)
        {
            old = _state;
            if (old == newState) return;
            _state = newState;
        }
        StateChanged?.Invoke(this, new HillClimbStateChangedEventArgs(old, newState));
        _log(LogLevel.Debug, $"HillClimb state: {old} -> {newState}");
    }

    private void RaiseStep(HillClimbPhase phase, int axisIndex, int direction,
        double stepSize_um, double vBefore, double vAfter, bool improvedGlobal)
    {
        StepCompleted?.Invoke(this, new HillClimbStepEventArgs(
            phase, axisIndex, direction, stepSize_um, vBefore, vAfter, improvedGlobal, _zCumulative_um));
    }

    private void RaiseLoop(int loopIdx, double zTraveled_um, double vLoopStart, double vLoopBest, bool improved)
    {
        LoopCompleted?.Invoke(this, new HillClimbLoopEventArgs(
            loopIdx, zTraveled_um, _zCumulative_um, vLoopStart, vLoopBest, _vPeakGlobal, improved));
    }

    private void RaiseProgress()
    {
        ProgressUpdated?.Invoke(this, new HillClimbProgressEventArgs(
            _zCumulative_um, _lastV, _vPeakGlobal, _currentZStep_um, _currentXYStep_um));
    }
}
