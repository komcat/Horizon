using Horizon.Engine;

namespace Horizon.Workflow.SpiralScan;

/// <summary>
/// Constant-pitch outward square-spiral signal search. Layer 2: drives a motion
/// device through <see cref="IHorizonEngine"/>'s motion facet and samples an
/// injected <see cref="ISpiralSearchFeedback"/> at each grid point, tracking the
/// running maximum. Sits at the same level as <see cref="HillClimb.HillClimbEngine"/>
/// and <see cref="Scanning.HexapodScanEngine"/> — pure code, no UI coupling; a
/// front-end constructs it directly.
///
/// The path is the classic square spiral in the configured plane (default X/Y):
/// starting at the current position it walks legs of length 1,1,2,2,3,3,… points,
/// each one <c>Pitch_um</c> long, turning +A → +B → −A → −B. So the arm spacing
/// equals the pitch and the swirl grows outward from the centre:
/// <code>
///   ↑ → → →
///   ↑ ┌───┐ ↓
///   ↑ │ • │ ↓      (• = start / centre)
///   ↑ └─← ┘ ↓
///   ← ← ← ← ↓
/// </code>
/// Stops when the next step would cross <c>MaxRadius_um</c> (hard), the point cap
/// is hit, or the running best reaches the Sagnac target. After any stop —
/// including cancel/error — the stage returns to the best point found (fail-safe).
/// </summary>
public sealed class SpiralSearchEngine
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;

    private SpiralSearchState _state = SpiralSearchState.Idle;
    private readonly object _stateLock = new();

    // Per-run context
    private string _device = string.Empty;
    private IReadOnlyList<string> _axes = Array.Empty<string>();
    private ISpiralSearchFeedback _feedback = null!;
    private SpiralSearchConfig _config = SpiralSearchConfig.Default;

    // Live state during a run
    private double _vStart;
    private double _vBest;
    private double[]? _posBest;
    private int _bestIa;
    private int _bestIb;
    private double _bestRadius_um;
    private double _lastV;
    private double _effectiveThreshold;
    private double _baselineSigma;
    private int _points;
    private double _currentRadius_um;

    public event EventHandler<SpiralSearchStateChangedEventArgs>? StateChanged;
    public event EventHandler<SpiralSearchBaselineRecordedEventArgs>? BaselineRecorded;
    public event EventHandler<SpiralSearchPointEventArgs>? PointSampled;
    public event EventHandler<SpiralSearchProgressEventArgs>? ProgressUpdated;
    public event EventHandler<SpiralSearchResult>? SearchCompleted;
    public event EventHandler<Exception>? ErrorOccurred;

    public SpiralSearchEngine(IHorizonEngine engine, Action<LogLevel, string>? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? ((_, _) => { });
    }

    public SpiralSearchState CurrentState
    {
        get { lock (_stateLock) return _state; }
    }

    public bool IsRunning
    {
        get
        {
            var s = CurrentState;
            return s is SpiralSearchState.RecordingBaseline
                or SpiralSearchState.Searching
                or SpiralSearchState.MovingToBest;
        }
    }

    /// <summary>
    /// Runs the spiral search on <paramref name="device"/> in the configured plane,
    /// maximising <paramref name="feedback"/>. Cancelling <paramref name="ct"/> halts
    /// the search; the stage still returns to the best position found.
    /// </summary>
    public Task<SpiralSearchResult> RunAsync(
        string device,
        ISpiralSearchFeedback feedback,
        SpiralSearchConfig config,
        CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("SpiralSearch is already running.");
        if (string.IsNullOrWhiteSpace(device)) throw new ArgumentException("Device required.", nameof(device));
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _config = (config ?? throw new ArgumentNullException(nameof(config))).Clone();
        _config.Validate();
        _device = device;

        return Task.Run(() => RunInternalAsync(ct), ct);
    }

    private async Task<SpiralSearchResult> RunInternalAsync(CancellationToken ct)
    {
        var result = new SpiralSearchResult { StartedAt = DateTime.Now };

        try
        {
            var deviceInfo = _engine.Motion.GetDevice(_device)
                ?? throw new InvalidOperationException($"Motion device '{_device}' not found.");
            _axes = deviceInfo.Axes;
            if (_config.AxisIndexA >= _axes.Count || _config.AxisIndexB >= _axes.Count)
                throw new InvalidOperationException(
                    $"Device '{_device}' has {_axes.Count} axes; configured plane axis indices exceed that.");
            if (!_engine.Motion.IsConnected(_device))
                throw new InvalidOperationException($"Device '{_device}' is not connected.");

            // ---- Baseline (centre) ----
            SetState(SpiralSearchState.RecordingBaseline);
            await RecordBaselineAsync(ct).ConfigureAwait(false);
            result.VStart = _vStart;
            result.BaselineSigma = _baselineSigma;
            result.EffectiveThreshold = _effectiveThreshold;

            // ---- Search ----
            SetState(SpiralSearchState.Searching);
            _vBest = _vStart;
            _posBest = await ReadPositionAsync(ct).ConfigureAwait(false);
            _bestIa = 0;
            _bestIb = 0;
            _bestRadius_um = 0;
            _points = 1; // the centre counts as the first sampled point
            RaisePoint(0, 0, 0, 0, _vStart, true);
            RaiseProgress();

            var stopReason = TargetReached()
                ? SpiralSearchStopReason.TargetReached
                : await WalkSpiralAsync(ct).ConfigureAwait(false);

            // ---- Return to best ----
            SetState(SpiralSearchState.MovingToBest);
            await MoveToAbsoluteAsync(_posBest!, ct).ConfigureAwait(false);
            await SettleAsync(ct).ConfigureAwait(false);

            FillResult(result, stopReason);
            result.Message = $"Stopped: {stopReason}. Best {_vBest:F6} (gain x{(_vStart > 0 ? _vBest / _vStart : 0):F2}) " +
                             $"at A{_bestIa * _config.Pitch_um:+0.0;-0.0} B{_bestIb * _config.Pitch_um:+0.0;-0.0} µm.";

            SetState(SpiralSearchState.Completed);
            SearchCompleted?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            FillResult(result, SpiralSearchStopReason.Cancelled);
            result.Message = "Cancelled.";

            // Best-effort return to best even on cancel (fresh token).
            try
            {
                if (_posBest != null && _engine.Motion.IsConnected(_device))
                {
                    SetState(SpiralSearchState.MovingToBest);
                    await MoveToAbsoluteAsync(_posBest, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log(LogLevel.Warning, $"SpiralSearch: failed to return to best after cancel: {ex.Message}");
            }

            SetState(SpiralSearchState.Cancelled);
            SearchCompleted?.Invoke(this, result);
            return result;
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, $"SpiralSearch error: {ex.Message}");
            FillResult(result, SpiralSearchStopReason.Error);
            result.Message = ex.Message;
            SetState(SpiralSearchState.Error);
            ErrorOccurred?.Invoke(this, ex);
            SearchCompleted?.Invoke(this, result);
            return result;
        }
    }

    /// <summary>
    /// Walks the square spiral from the centre outward. Bounded by
    /// <see cref="SpiralSearchConfig.MaxPoints"/> (a fixed cap — Power of 10 Rule 2)
    /// with <see cref="SpiralSearchConfig.MaxRadius_um"/> as the normal stop.
    /// </summary>
    private async Task<SpiralSearchStopReason> WalkSpiralAsync(CancellationToken ct)
    {
        double pitchMm = _config.Pitch_um / 1000.0;
        int ia = 0, ib = 0;   // integer grid offset from centre (in pitch units)
        int legLen = 1;       // points in the current leg
        int dir = 0;          // 0:+A 1:+B 2:-A 3:-B
        int legsAtLen = 0;    // two legs per length, then legLen++

        while (_points < _config.MaxPoints)
        {
            for (int s = 0; s < legLen; s++)
            {
                ct.ThrowIfCancellationRequested();

                // Prospective next grid cell for this direction.
                (int nia, int nib) = Advance(ia, ib, dir);
                double radius_um = _config.Pitch_um * Math.Sqrt((double)nia * nia + (double)nib * nib);
                if (radius_um > _config.MaxRadius_um)
                    return SpiralSearchStopReason.RadiusReached;

                bool alongA = dir is 0 or 2;
                bool positive = dir is 0 or 1;
                int axisIndex = alongA ? _config.AxisIndexA : _config.AxisIndexB;
                await MoveAxisAsync(axisIndex, positive ? pitchMm : -pitchMm, ct).ConfigureAwait(false);
                ia = nia;
                ib = nib;

                await SettleAsync(ct).ConfigureAwait(false);
                double[] pos = await ReadPositionAsync(ct).ConfigureAwait(false);
                double v = await SampleAsync(ct).ConfigureAwait(false);
                _points++;
                _currentRadius_um = radius_um;

                bool isBest = TryUpdateBest(v, pos, ia, ib, radius_um);
                RaisePoint(_points - 1, ia * _config.Pitch_um, ib * _config.Pitch_um, radius_um, v, isBest);
                RaiseProgress();

                if (TargetReached())
                    return SpiralSearchStopReason.TargetReached;
                if (_points >= _config.MaxPoints)
                    return SpiralSearchStopReason.MaxPointsReached;
            }

            dir = (dir + 1) & 3;
            if (++legsAtLen == 2)
            {
                legsAtLen = 0;
                legLen++;
            }
        }

        return SpiralSearchStopReason.MaxPointsReached;
    }

    /// <summary>Advance a grid offset by one cell in direction <paramref name="dir"/> (0:+A 1:+B 2:-A 3:-B).</summary>
    private static (int ia, int ib) Advance(int ia, int ib, int dir) => dir switch
    {
        0 => (ia + 1, ib),
        1 => (ia, ib + 1),
        2 => (ia - 1, ib),
        _ => (ia, ib - 1),
    };

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

        _log(LogLevel.Info, $"SpiralSearch baseline: V={_vStart:F6}, sigma={_baselineSigma:F6}, effThreshold={_effectiveThreshold:P2} ({_feedback.Description}).");

        BaselineRecorded?.Invoke(this, new SpiralSearchBaselineRecordedEventArgs(_vStart, _baselineSigma, _effectiveThreshold));
    }

    /// <summary>Records a new global best if <paramref name="v"/> beats it by the effective threshold.</summary>
    private bool TryUpdateBest(double v, double[] pos, int ia, int ib, double radius_um)
    {
        if (v <= _vBest * (1 + _effectiveThreshold)) return false;
        _vBest = v;
        _posBest = (double[])pos.Clone();
        _bestIa = ia;
        _bestIb = ib;
        _bestRadius_um = radius_um;
        return true;
    }

    /// <summary>
    /// True once the running best has reached the configured Sagnac target fraction
    /// (<c>VBest ≥ TargetSagnacV · TargetReachedFraction</c>). Disabled when
    /// <see cref="SpiralSearchConfig.TargetSagnacV"/> is 0.
    /// </summary>
    private bool TargetReached()
        => _config.TargetSagnacV > 0
           && _vBest >= _config.TargetSagnacV * _config.TargetReachedFraction;

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
        double mean = sum / n;
        _lastV = mean;
        return mean;
    }

    /// <summary>
    /// Reads the feedback and validates it is finite (Power of 10 Rule 7 — validate
    /// inputs at the boundary). A NaN/∞ from the sensor would poison the best
    /// comparison (∞ would falsely win and park the stage there), so we fail fast;
    /// the run's top-level handler returns the stage to the last known best.
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
    /// Runs a hardware operation under a per-op time bound (<see cref="SpiralSearchConfig.IoTimeoutMs"/>).
    /// Power of 10 Rule 2 in the time domain: every I/O completes within a fixed bound, so a hung
    /// controller or feedback link can't stall the search forever despite the point cap. A timeout
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

    private async Task SettleAsync(CancellationToken ct)
    {
        if (_config.SettleMs > 0)
            await Task.Delay(_config.SettleMs, ct).ConfigureAwait(false);
    }

    private void FillResult(SpiralSearchResult result, SpiralSearchStopReason stopReason)
    {
        result.StopReason = stopReason;
        result.VStart = _vStart;
        result.VBest = _vBest;
        result.PosBest = _posBest != null ? ToPosition(_posBest) : null;
        result.BestOffsetA_um = _bestIa * _config.Pitch_um;
        result.BestOffsetB_um = _bestIb * _config.Pitch_um;
        result.BestRadius_um = _bestRadius_um;
        result.PointsVisited = _points;
        result.BaselineSigma = _baselineSigma;
        result.EffectiveThreshold = _effectiveThreshold;
        result.FinishedAt = DateTime.Now;
    }

    private void SetState(SpiralSearchState newState)
    {
        SpiralSearchState old;
        lock (_stateLock)
        {
            old = _state;
            if (old == newState) return;
            _state = newState;
        }
        StateChanged?.Invoke(this, new SpiralSearchStateChangedEventArgs(old, newState));
        _log(LogLevel.Debug, $"SpiralSearch state: {old} -> {newState}");
    }

    private void RaisePoint(int pointIndex, double offsetA_um, double offsetB_um, double radius_um, double v, bool isBest)
    {
        PointSampled?.Invoke(this, new SpiralSearchPointEventArgs(pointIndex, offsetA_um, offsetB_um, radius_um, v, isBest));
    }

    private void RaiseProgress()
    {
        ProgressUpdated?.Invoke(this, new SpiralSearchProgressEventArgs(_points, _currentRadius_um, _lastV, _vBest));
    }
}
