using Horizon.Common;
using Horizon.Engine;
using Horizon.Workflow.HillClimb;

namespace Horizon.Workflow.GridSearch;

/// <summary>
/// Post-FAU-pre-align signal finder over the hexapod XY plane, maximising the SiPhOG Sagnac
/// signal through the shared <see cref="IHillClimbFeedback"/> seam (the same <c>SAG PWR (V)</c>
/// the Hill Climber and Auto-W use). Two stages:
///
/// <para><b>Stage 1 — X line search.</b> Record a baseline at centre, jump +<c>LineJumpUm</c> in X,
/// then sweep back in −X by <c>LineStepUm</c> for <c>LineSteps</c> steps. A reading that clears
/// <c>vBase + DetectDeltaV</c> means "signal seen"; once seen, the sweep ends early when it has
/// clearly walked off the crest (drop &gt; <c>DropFraction · (peak − vBase)</c> for
/// <c>DropConfirmCount</c> readings). On signal, the stage parks at the line peak.</para>
///
/// <para><b>Stage 2 — XY grid fallback.</b> Only if Stage 1 saw nothing: a serpentine raster over
/// ±<c>GridExtentUm</c> at <c>GridStepUm</c> pitch (bounded by <c>MaxGridNodes</c>), stopping at the
/// first node that clears the detect level and parking there.</para>
///
/// <para>On a hit in either stage the run ends with <see cref="GridSearchResult.SignalFound"/> set
/// so the caller (the Run-page panel) can hand off to the <see cref="HillClimbEngine"/> from the
/// parked pose. If neither stage finds signal, the hexapod is parked at the best point seen and the
/// result reports "no signal".</para>
///
/// Pure code, no UI coupling — mirrors <see cref="HillClimbEngine"/>/<c>AutoWEngine</c>. Cancelling
/// the token halts the search and still parks the hexapod at the best pose found (fail-safe,
/// Power of 10 Rule 7).
/// </summary>
public sealed class FauGridSearchEngine
{
    private readonly IHorizonEngine _engine;
    private readonly Action<LogLevel, string> _log;

    private GridSearchState _state = GridSearchState.Idle;
    private readonly object _stateLock = new();

    // Per-run context
    private string _device = string.Empty;
    private IReadOnlyList<string> _axes = Array.Empty<string>();
    private IHillClimbFeedback _feedback = null!;
    private GridSearchConfig _config = GridSearchConfig.Default;

    // Live state during a run
    private double _vBase;
    private double _baselineSigma;
    private double _detectLevel;
    private double[] _posCentre = Array.Empty<double>();
    private double _peakV;
    private double[]? _posPeak;
    private int _pointsMeasured;
    private int _measIndex;

    public event EventHandler<GridSearchStateChangedEventArgs>? StateChanged;
    public event EventHandler<GridSearchBaselineRecordedEventArgs>? BaselineRecorded;
    public event EventHandler<GridSearchMeasuredEventArgs>? Measured;
    public event EventHandler<GridSearchResult>? Completed;
    public event EventHandler<Exception>? ErrorOccurred;

    public FauGridSearchEngine(IHorizonEngine engine, Action<LogLevel, string>? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? ((_, _) => { });
    }

    public GridSearchState CurrentState
    {
        get { lock (_stateLock) return _state; }
    }

    public bool IsRunning
    {
        get
        {
            var s = CurrentState;
            return s is GridSearchState.RecordingBaseline
                or GridSearchState.LineSearch
                or GridSearchState.GridSearch
                or GridSearchState.MovingToPeak;
        }
    }

    /// <summary>
    /// Runs the two-stage search on <paramref name="hexapodDevice"/>, maximising
    /// <paramref name="feedback"/>. Cancelling <paramref name="ct"/> halts the search; the hexapod
    /// still returns to the best pose found.
    /// </summary>
    public Task<GridSearchResult> RunAsync(
        string hexapodDevice,
        IHillClimbFeedback feedback,
        GridSearchConfig config,
        CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Grid search is already running.");
        if (string.IsNullOrWhiteSpace(hexapodDevice)) throw new ArgumentException("Device required.", nameof(hexapodDevice));
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _config = (config ?? throw new ArgumentNullException(nameof(config))).Clone();
        _config.Validate();
        _device = hexapodDevice;

        return Task.Run(() => RunInternalAsync(ct), ct);
    }

    private async Task<GridSearchResult> RunInternalAsync(CancellationToken ct)
    {
        var result = new GridSearchResult { StartedAt = DateTime.Now };
        _peakV = double.NegativeInfinity;
        _posPeak = null;
        _pointsMeasured = 0;
        _measIndex = 0;

        try
        {
            var deviceInfo = _engine.Motion.GetDevice(_device)
                ?? throw new InvalidOperationException($"Motion device '{_device}' not found.");
            _axes = deviceInfo.Axes;
            int maxAxis = Math.Max(_config.LineAxisIndex, Math.Max(_config.GridXAxisIndex, _config.GridYAxisIndex));
            if (maxAxis >= _axes.Count)
                throw new InvalidOperationException(
                    $"Hexapod '{_device}' has {_axes.Count} axes; configured axis indices exceed that.");
            if (!_engine.Motion.IsConnected(_device))
                throw new InvalidOperationException($"Hexapod '{_device}' is not connected.");

            // Drive the whole search at the configured slow system velocity.
            var vel = await WithTimeoutAsync(
                _ => _engine.Motion.SetDeviceVelocityAsync(_device, _config.SystemVelocity_mmPerS),
                "Set system velocity", ct).ConfigureAwait(false);
            if (!vel.IsSuccess)
                _log(LogLevel.Warning, $"Grid search: could not set system velocity ({vel.Message}); continuing at current speed.");

            // ---- Baseline at centre ----
            SetState(GridSearchState.RecordingBaseline);
            await RecordBaselineAsync(ct).ConfigureAwait(false);
            _detectLevel = _vBase + _config.DetectDeltaV;
            _posCentre = await ReadPositionAsync(ct).ConfigureAwait(false);
            UpdatePeak(_vBase, _posCentre);   // seed the peak at the centre baseline
            result.VBaseline = _vBase;
            result.BaselineSigma = _baselineSigma;
            result.DetectLevel = _detectLevel;
            BaselineRecorded?.Invoke(this, new GridSearchBaselineRecordedEventArgs(_vBase, _baselineSigma, _detectLevel));
            _log(LogLevel.Info,
                $"Grid search baseline: V={_vBase:F6}, σ={_baselineSigma:F6}, detect ≥ {_detectLevel:F6} V ({_feedback.Description}).");

            // ---- Stage 1: X line ----
            SetState(GridSearchState.LineSearch);
            bool signalSeen = await LineSearchAsync(ct).ConfigureAwait(false);

            if (signalSeen)
            {
                await ParkAtAsync(_posPeak!, ct).ConfigureAwait(false);
                FillResult(result, GridSearchStopReason.SignalFoundLine, signalFound: true);
                result.Message = $"Stage 1: signal found — peak {_peakV:F6} V at line ({result.PointsMeasured} pts). Hand off to hill climb.";
            }
            else
            {
                // ---- Stage 2: XY grid fallback ----
                SetState(GridSearchState.GridSearch);
                double[]? hit = await GridSearchAsync(ct).ConfigureAwait(false);

                if (hit != null)
                {
                    await ParkAtAsync(hit, ct).ConfigureAwait(false);
                    FillResult(result, GridSearchStopReason.SignalFoundGrid, signalFound: true);
                    result.Message = $"Stage 2: signal found — {_peakV:F6} V at grid node ({result.PointsMeasured} pts). Hand off to hill climb.";
                }
                else
                {
                    await ParkAtAsync(_posPeak!, ct).ConfigureAwait(false);
                    FillResult(result, GridSearchStopReason.NoSignal, signalFound: false);
                    result.Message = $"No signal found across line + grid ({result.PointsMeasured} pts); parked at best {_peakV:F6} V.";
                }
            }

            SetState(GridSearchState.Completed);
            Completed?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            FillResult(result, GridSearchStopReason.Cancelled, signalFound: false);
            result.Message = "Cancelled.";

            // Best-effort return to the best pose even on cancel (fresh token).
            try
            {
                if (_posPeak != null && _engine.Motion.IsConnected(_device))
                {
                    SetState(GridSearchState.MovingToPeak);
                    await MoveAbsoluteAsync(_posPeak, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log(LogLevel.Warning, $"Grid search: failed to return to peak after cancel: {ex.Message}");
            }

            SetState(GridSearchState.Cancelled);
            Completed?.Invoke(this, result);
            return result;
        }
        catch (Exception ex)
        {
            _log(LogLevel.Error, $"Grid search error: {ex.Message}");
            FillResult(result, GridSearchStopReason.Error, signalFound: false);
            result.Message = ex.Message;

            // Fail safe (Power of 10 Rule 7): return the stage to the last known-good pose on any
            // abort, not just cancel. Best-effort with a fresh token so a faulted run still parks.
            try
            {
                if (_posPeak != null && _engine.Motion.IsConnected(_device))
                {
                    SetState(GridSearchState.MovingToPeak);
                    await MoveAbsoluteAsync(_posPeak, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception parkEx)
            {
                _log(LogLevel.Warning, $"Grid search: failed to return to peak after error: {parkEx.Message}");
            }

            SetState(GridSearchState.Error);
            ErrorOccurred?.Invoke(this, ex);
            Completed?.Invoke(this, result);
            return result;
        }
    }

    /// <summary>
    /// Stage 1: jump +X to the apex, then sweep −X step by step. Tracks the peak, flags signal once
    /// a reading clears the detect level, and exits early once the sweep has confirmedly walked off
    /// the crest. Returns true when signal was seen.
    /// </summary>
    private async Task<bool> LineSearchAsync(CancellationToken ct)
    {
        int axis = _config.LineAxisIndex;
        double centreAxis = _posCentre[axis];
        double jumpMm = _config.LineJumpUm / 1000.0;
        double stepMm = _config.LineStepUm / 1000.0;

        bool signalSeen = false;
        int dipCount = 0;

        // Apex: absolute move to centre + jump (drift-free; one axis changed).
        await MoveToPeakOrThrowAsync(WithAxis(_posCentre, axis, centreAxis + jumpMm), "line apex", ct).ConfigureAwait(false);
        await SettleAsync(ct).ConfigureAwait(false);
        {
            double v = await SampleAsync(ct).ConfigureAwait(false);
            double[] pos = await ReadPositionAsync(ct).ConfigureAwait(false);
            UpdatePeak(v, pos);
            if (v > _detectLevel) signalSeen = true;
            RaiseMeasured(GridSearchPhase.Line, $"+{_config.LineJumpUm:0.#} µm", v, signalSeen);
        }

        // Downward sweep: apex − k·step, in absolute coordinates from the captured centre.
        for (int k = 1; k <= _config.LineSteps; k++)
        {
            ct.ThrowIfCancellationRequested();

            double offsetUm = _config.LineJumpUm - (k * _config.LineStepUm);
            await MoveToPeakOrThrowAsync(
                WithAxis(_posCentre, axis, centreAxis + jumpMm - (k * stepMm)), "line step", ct).ConfigureAwait(false);
            await SettleAsync(ct).ConfigureAwait(false);
            double v = await SampleAsync(ct).ConfigureAwait(false);
            double[] pos = await ReadPositionAsync(ct).ConfigureAwait(false);

            UpdatePeak(v, pos);
            if (v > _detectLevel) signalSeen = true;

            // Past-crest guardrail: drop measured against the peak's height above baseline, which is
            // ≥ DetectDeltaV > 0 once signalSeen — well-defined for any baseline sign.
            if (signalSeen && (_peakV - v) > _config.DropFraction * (_peakV - _vBase))
            {
                dipCount++;
                RaiseMeasured(GridSearchPhase.Line, $"{offsetUm:+0.#;-0.#} µm", v, signalSeen);
                if (dipCount >= _config.DropConfirmCount)
                {
                    _log(LogLevel.Info, $"Grid search: crest passed at line offset {offsetUm:+0.#;-0.#} µm — ending sweep early.");
                    break;
                }
                continue;
            }

            dipCount = 0;
            RaiseMeasured(GridSearchPhase.Line, $"{offsetUm:+0.#;-0.#} µm", v, signalSeen);
        }

        return signalSeen;
    }

    /// <summary>
    /// Stage 2: serpentine XY raster about the captured centre. Returns the pose of the first node
    /// that clears the detect level (hand-off point), or null if no node did. Tracks the peak so the
    /// caller can park at the best point on a total miss.
    /// </summary>
    private async Task<double[]?> GridSearchAsync(CancellationToken ct)
    {
        int ax = _config.GridXAxisIndex, ay = _config.GridYAxisIndex;
        double stepMm = _config.GridStepUm / 1000.0;
        double cx = _posCentre[ax], cy = _posCentre[ay];

        int n = (int)Math.Floor(_config.GridExtentUm / _config.GridStepUm);
        int perAxis = (2 * n) + 1;
        long totalNodes = (long)perAxis * perAxis;
        if (totalNodes > _config.MaxGridNodes)
            throw new InvalidOperationException(
                $"Grid {perAxis}×{perAxis} = {totalNodes} nodes exceeds the {_config.MaxGridNodes}-node cap — " +
                "increase GridStepUm or reduce GridExtentUm.");

        _log(LogLevel.Info,
            $"Grid search Stage 2: {perAxis}×{perAxis} raster, {_config.GridStepUm:0.#} µm pitch, ±{_config.GridExtentUm:0.#} µm ({totalNodes} nodes).");

        // Serpentine raster: rows along Y, columns along X, flipping column direction each row.
        for (int iy = -n; iy <= n; iy++)
        {
            ct.ThrowIfCancellationRequested();
            bool forward = (iy + n) % 2 == 0;

            for (int col = 0; col < perAxis; col++)
            {
                ct.ThrowIfCancellationRequested();
                int ix = forward ? (-n + col) : (n - col);

                double[] target = WithAxis(WithAxis(_posCentre, ax, cx + (ix * stepMm)), ay, cy + (iy * stepMm));
                var move = await MoveAbsoluteAsync(target, ct).ConfigureAwait(false);
                if (!move.IsSuccess)
                {
                    // A plain motion failure skips the node (matches GridScanStrategy); cancel surfaces above.
                    ct.ThrowIfCancellationRequested();
                    _log(LogLevel.Warning, $"Grid search: move to node ({ix},{iy}) failed — skipping ({move.Message}).");
                    continue;
                }

                await SettleAsync(ct).ConfigureAwait(false);
                double v = await SampleAsync(ct).ConfigureAwait(false);
                double[] pos = await ReadPositionAsync(ct).ConfigureAwait(false);
                UpdatePeak(v, pos);

                bool hit = v > _detectLevel;
                RaiseMeasured(GridSearchPhase.Grid, $"({ix},{iy})", v, hit);

                if (hit)
                {
                    _log(LogLevel.Info, $"Grid search: signal at node ({ix},{iy}) = {v:F6} V — handing off to hill climb.");
                    return pos;
                }
            }
        }

        return null;
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
        _vBase = mean;
        _baselineSigma = Math.Sqrt(variance);
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
        return sum / n;
    }

    /// <summary>Updates the global peak (raw max — no relative threshold; absolute detect handles noise).</summary>
    private void UpdatePeak(double v, double[] pos)
    {
        if (_posPeak == null || v > _peakV)
        {
            _peakV = v;
            _posPeak = (double[])pos.Clone();
        }
    }

    /// <summary>
    /// Reads the feedback and validates it is finite (Power of 10 Rule 7 — validate inputs at the
    /// boundary). A NaN/∞ would poison the peak comparisons (∞ would falsely win and park the stage
    /// there), so we fail fast; the run handler returns the stage to the last peak.
    /// </summary>
    private double ReadFeedbackChecked()
    {
        double v = _feedback.Read();
        if (!double.IsFinite(v))
            throw new InvalidOperationException(
                $"Feedback '{_feedback.Description}' returned a non-finite value ({v}).");
        return v;
    }

    /// <summary>Absolute move that throws on failure — used for the line sweep + final park (fail-safe upstream).</summary>
    private async Task MoveToPeakOrThrowAsync(double[] pos, string what, CancellationToken ct)
    {
        var move = await MoveAbsoluteAsync(pos, ct).ConfigureAwait(false);
        if (!move.IsSuccess)
            throw new InvalidOperationException($"{what} move failed: {move.Message}");
    }

    private async Task ParkAtAsync(double[] pos, CancellationToken ct)
    {
        SetState(GridSearchState.MovingToPeak);
        await MoveToPeakOrThrowAsync(pos, "park at peak", ct).ConfigureAwait(false);
        await SettleAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Absolute move returning the raw <see cref="Result"/> (no throw) under the I/O time bound.</summary>
    private Task<Result> MoveAbsoluteAsync(double[] pos, CancellationToken ct)
        => WithTimeoutAsync(
            token => _engine.Motion.MoveToPositionAsync(_device, ToPosition(pos), token), "Move", ct);

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
    /// Runs a hardware operation under a per-op time bound (<see cref="GridSearchConfig.IoTimeoutMs"/>).
    /// Power of 10 Rule 2 (temporal): a hung controller or feedback socket can't stall the search
    /// forever. A timeout surfaces as <see cref="TimeoutException"/> (caught by the run handler →
    /// returns to the last peak); a caller cancel still propagates as cancellation.
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

    private void FillResult(GridSearchResult result, GridSearchStopReason reason, bool signalFound)
    {
        result.StopReason = reason;
        result.SignalFound = signalFound;
        result.VPeak = double.IsNegativeInfinity(_peakV) ? 0 : _peakV;
        result.PosPeak = _posPeak != null ? ToPosition(_posPeak) : null;
        result.PointsMeasured = _pointsMeasured;
        result.FinishedAt = DateTime.Now;
    }

    private static double[] WithAxis(double[] pos, int idx, double value)
    {
        var c = (double[])pos.Clone();
        c[idx] = value;
        return c;
    }

    private static MotionPosition ToPosition(double[] p)
        => new(p[0], p[1], p[2], p[3], p[4], p[5]);

    private async Task SettleAsync(CancellationToken ct)
    {
        if (_config.SettleMs > 0)
            await Task.Delay(_config.SettleMs, ct).ConfigureAwait(false);
    }

    private void SetState(GridSearchState newState)
    {
        GridSearchState old;
        lock (_stateLock)
        {
            old = _state;
            if (old == newState) return;
            _state = newState;
        }
        StateChanged?.Invoke(this, new GridSearchStateChangedEventArgs(old, newState));
        _log(LogLevel.Debug, $"Grid search state: {old} -> {newState}");
    }

    private void RaiseMeasured(GridSearchPhase phase, string label, double v, bool signalSeen)
    {
        _pointsMeasured++;
        Measured?.Invoke(this, new GridSearchMeasuredEventArgs(_measIndex++, phase, label, v, _peakV, signalSeen));
    }
}
