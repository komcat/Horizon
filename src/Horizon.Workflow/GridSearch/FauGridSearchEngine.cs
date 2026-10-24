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
/// then sweep back in −X by <c>LineStepUm</c> for <c>LineSteps</c> steps. "Signal" is a significant
/// rise over the unit's own noise floor — the running median of the readings (dark level, any sign)
/// plus <c>max(NoiseK · baseline σ, DetectDeltaV)</c>. With <c>StopOnSignal</c> the search re-measures
/// the first significant point and stops there; otherwise the sweep ends early when it has clearly
/// walked off the crest (drop &gt; <c>DropFraction · (peak − floor)</c> for
/// <c>DropConfirmCount</c> readings). On signal, the stage parks at the line peak. With
/// <c>LineYSweepEnabled</c>, every −X step is followed by a Y sweep ±<c>LineYRangeUm</c> at
/// <c>LineYStepUm</c> pitch (the crest test then uses the best reading of that X column). Any
/// reading above <c>ExitAboveV</c> ends the search at once, parked at that point.</para>
///
/// <para><b>Stage 2 — Y+ line-scan fallback.</b> Only if Stage 1 saw nothing: step Y+ for
/// <c>GridYRows</c> rows at <c>GridStepUm</c> pitch (centre upward only, never −Y) and run a
/// Stage-1-style X line scan at each row (bounded by <c>MaxGridNodes</c>), stopping at the first row
/// that clears the detect level and parking at that row's X peak.</para>
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
    private int _peakIndex;
    private int _pointsMeasured;
    private int _measIndex;
    private bool _exitHit;
    private bool _signalStop;

    // Noise floor: every reading (baseline first) and the same values ascending — the floor is their
    // median (the unit's dark level, whatever its sign), the baseline alone until 3 readings.
    private readonly List<(double V, double[] Pos)> _points = new();
    private readonly List<double> _sorted = new();
    private double _delta;   // significant rise over the floor: max(NoiseK × σ, DetectDeltaV)

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
        _peakIndex = 0;
        _pointsMeasured = 0;
        _measIndex = 0;
        _exitHit = false;
        _signalStop = false;
        _points.Clear();
        _sorted.Clear();

        try
        {
            var deviceInfo = _engine.Motion.GetDevice(_device)
                ?? throw new InvalidOperationException($"Motion device '{_device}' not found.");
            _axes = deviceInfo.Axes;
            int maxAxis = Math.Max(_config.LineAxisIndex, _config.GridYAxisIndex);
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
            _delta = Math.Max(_config.NoiseK * _baselineSigma, _config.DetectDeltaV);
            _detectLevel = _vBase + _delta;   // the level while the floor is still the baseline
            _posCentre = await ReadPositionAsync(ct).ConfigureAwait(false);
            AddReading(_vBase, _posCentre);   // seed the floor + peak at the centre baseline
            result.VBaseline = _vBase;
            result.BaselineSigma = _baselineSigma;
            result.DetectLevel = _detectLevel;
            BaselineRecorded?.Invoke(this, new GridSearchBaselineRecordedEventArgs(_vBase, _baselineSigma, _detectLevel));
            _log(LogLevel.Info,
                $"Grid search baseline: V={_vBase:F6}, σ={_baselineSigma:F6}; signal = noise floor (median of readings) + " +
                $"{_delta:F6} V (max of {_config.NoiseK:0.##}σ, {_config.DetectDeltaV:0.####} V); " +
                $"{(_config.StopOnSignal ? "stop at the first confirmed signal" : "finish the line")} ({_feedback.Description}).");

            // ---- Stage 1: X line (skipped when disabled) ----
            bool signalSeen = false;
            if (CheckExit(_vBase, "centre"))
            {
                signalSeen = true;   // already above the stop level at centre — no search needed
            }
            else if (_config.Stage1Enabled)
            {
                SetState(GridSearchState.LineSearch);
                signalSeen = await LineSearchAsync(_posCentre, GridSearchPhase.Line, string.Empty,
                    _config.LineYSweepEnabled, ct).ConfigureAwait(false);
            }
            else
            {
                _log(LogLevel.Info, "Grid search: Stage 1 (X line) disabled — skipping to Stage 2.");
            }

            if (signalSeen)
            {
                await ParkAtAsync(_posPeak!, ct).ConfigureAwait(false);
                FillResult(result, GridSearchStopReason.SignalFoundLine, signalFound: true);
                result.Message = _exitHit
                    ? $"Stage 1: {_peakV:F6} V > exit {_config.ExitAboveV:0.###} V — stopped there ({result.PointsMeasured} pts). Hand off to hill climb."
                    : _signalStop
                        ? $"Stage 1: signal {_peakV:F6} V over floor {Floor():F6} V — stopped there ({result.PointsMeasured} pts). Hand off to hill climb."
                        : $"Stage 1: signal found — peak {_peakV:F6} V at line ({result.PointsMeasured} pts). Hand off to hill climb.";
            }
            else
            {
                // ---- Stage 2: XY grid fallback (skipped when disabled) ----
                double[]? hit = null;
                if (_config.Stage2Enabled)
                {
                    SetState(GridSearchState.GridSearch);
                    hit = await GridSearchAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    _log(LogLevel.Info, "Grid search: Stage 2 (XY grid) disabled — not running grid fallback.");
                }

                if (hit != null)
                {
                    await ParkAtAsync(hit, ct).ConfigureAwait(false);
                    FillResult(result, GridSearchStopReason.SignalFoundGrid, signalFound: true);
                    result.Message = $"Stage 2: signal found — {_peakV:F6} V in a Y+ row ({result.PointsMeasured} pts). Hand off to hill climb.";
                }
                else
                {
                    await ParkAtAsync(_posPeak!, ct).ConfigureAwait(false);
                    FillResult(result, GridSearchStopReason.NoSignal, signalFound: false);
                    result.Message = $"No signal found ({result.PointsMeasured} pts); parked at best {_peakV:F6} V.";
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
    /// One X line scan about <paramref name="rowCentre"/>: jump +X to the apex, then sweep −X step by
    /// step. Tracks the peak, flags signal once a reading clears the detect level, and exits early
    /// once the sweep has confirmedly walked off the crest. Returns true when signal was seen. Shared
    /// by Stage 1 (row centre = the captured centre) and Stage 2 (one call per Y+ row, with the row's
    /// Y already baked into <paramref name="rowCentre"/>). <paramref name="rowLabel"/> prefixes the
    /// measured-event labels (empty for Stage 1). With <paramref name="ySweep"/>, each −X step is
    /// followed by a Y sweep ±<c>LineYRangeUm</c> about the row's Y. Any reading above
    /// <c>ExitAboveV</c> returns at once (signal seen, hexapod left at that point = the peak).
    /// </summary>
    private async Task<bool> LineSearchAsync(double[] rowCentre, GridSearchPhase phase, string rowLabel,
        bool ySweep, CancellationToken ct)
    {
        int axis = _config.LineAxisIndex;
        int yAxis = _config.GridYAxisIndex;
        double centreAxis = rowCentre[axis];
        double jumpMm = _config.LineJumpUm / 1000.0;
        double stepMm = _config.LineStepUm / 1000.0;
        int yHalf = ySweep ? _config.LineYHalfSteps : 0;

        bool signalSeen = false;
        int dipCount = 0;

        // Apex: absolute move to row centre + jump (drift-free; X set, the row Y already in rowCentre).
        await MoveToPeakOrThrowAsync(WithAxis(rowCentre, axis, centreAxis + jumpMm), "line apex", ct).ConfigureAwait(false);
        {
            string label = RowLabel(rowLabel, $"+{_config.LineJumpUm:0.#} µm");
            double v = await MeasureHereAsync(ct).ConfigureAwait(false);
            if (Significant(v)) signalSeen = true;
            RaiseMeasured(phase, label, v, signalSeen);
            if (CheckExit(v, label) || await SignalStopAsync(ct).ConfigureAwait(false)) return true;
        }

        // Downward sweep: apex − k·step, in absolute coordinates from the row centre.
        for (int k = 1; k <= _config.LineSteps; k++)
        {
            ct.ThrowIfCancellationRequested();

            double offsetUm = _config.LineJumpUm - (k * _config.LineStepUm);
            string xLabel = RowLabel(rowLabel, $"{offsetUm:+0.#;-0.#} µm");
            double[] stepPos = WithAxis(rowCentre, axis, centreAxis + jumpMm - (k * stepMm));
            await MoveToPeakOrThrowAsync(stepPos, "line step", ct).ConfigureAwait(false);
            double v = await MeasureHereAsync(ct).ConfigureAwait(false);
            if (Significant(v)) signalSeen = true;
            RaiseMeasured(phase, xLabel, v, signalSeen);
            if (CheckExit(v, xLabel) || await SignalStopAsync(ct).ConfigureAwait(false)) return true;

            // Y sweep at this X: −range … +range (centre already measured above). The next X step's
            // absolute move returns Y to the row centre.
            double columnBest = v;
            for (int j = -yHalf; j <= yHalf; j++)
            {
                if (j == 0) continue;
                ct.ThrowIfCancellationRequested();

                double yUm = j * _config.LineYStepUm;
                string yLabel = $"{xLabel} Y{yUm:+0.#;-0.#} µm";
                await MoveToPeakOrThrowAsync(
                    WithAxis(stepPos, yAxis, stepPos[yAxis] + (yUm / 1000.0)), "Y sweep step", ct).ConfigureAwait(false);
                double vy = await MeasureHereAsync(ct).ConfigureAwait(false);
                if (Significant(vy)) signalSeen = true;
                RaiseMeasured(phase, yLabel, vy, signalSeen);
                if (CheckExit(vy, yLabel) || await SignalStopAsync(ct).ConfigureAwait(false)) return true;
                columnBest = Math.Max(columnBest, vy);
            }

            // Past-crest guardrail: drop measured against the peak's height above the noise floor,
            // which is > 0 once signalSeen — well-defined for any floor sign. With a Y sweep the
            // column's best reading stands for this X step.
            if (signalSeen && (_peakV - columnBest) > _config.DropFraction * (_peakV - Floor()))
            {
                dipCount++;
                if (dipCount >= _config.DropConfirmCount)
                {
                    _log(LogLevel.Info, $"Grid search: crest passed at line offset {offsetUm:+0.#;-0.#} µm — ending sweep early.");
                    break;
                }
                continue;
            }

            dipCount = 0;
        }

        return signalSeen;
    }

    /// <summary>Settle, sample, and fold the reading into the peak tracker at the current pose.</summary>
    private async Task<double> MeasureHereAsync(CancellationToken ct)
    {
        await SettleAsync(ct).ConfigureAwait(false);
        double v = await SampleAsync(ct).ConfigureAwait(false);
        double[] pos = await ReadPositionAsync(ct).ConfigureAwait(false);
        AddReading(v, pos);
        return v;
    }

    /// <summary>
    /// With <see cref="GridSearchConfig.StopOnSignal"/>: when the best point so far is significant
    /// over the current floor, go back to it and re-measure. Confirmed → true (the search stops; the
    /// caller parks there). Not confirmed → that point keeps its re-read value and the search goes on,
    /// so a one-frame glitch can't end it.
    /// </summary>
    private async Task<bool> SignalStopAsync(CancellationToken ct)
    {
        if (!_config.StopOnSignal || _posPeak == null || !Significant(_peakV)) return false;

        double was = _peakV;
        await MoveToPeakOrThrowAsync(_posPeak, "confirm signal", ct).ConfigureAwait(false);
        await SettleAsync(ct).ConfigureAwait(false);
        double again = await SampleAsync(ct).ConfigureAwait(false);
        double floor = Floor();
        if (again > floor + _delta)
        {
            _signalStop = true;
            _log(LogLevel.Info,
                $"Grid search: signal {was:F6} V confirmed (re-read {again:F6} V > floor {floor:F6} + {_delta:F6} V) — stopping search.");
            return true;
        }

        _log(LogLevel.Info,
            $"Grid search: {was:F6} V not confirmed (re-read {again:F6} V, floor {floor:F6} + {_delta:F6} V) — continuing.");
        ReplacePeakReading(again);
        return false;
    }

    /// <summary>Noise floor: median of every reading so far; the baseline until there are 3.</summary>
    private double Floor()
    {
        int n = _sorted.Count;
        if (n < 3) return _vBase;
        return n % 2 == 1 ? _sorted[n / 2] : (_sorted[(n / 2) - 1] + _sorted[n / 2]) / 2;
    }

    /// <summary>True when <paramref name="v"/> is a significant rise over the noise floor.</summary>
    private bool Significant(double v) => v > Floor() + _delta;

    /// <summary>Records a reading (floor + peak). The position is copied.</summary>
    private void AddReading(double v, double[] pos)
    {
        _points.Add((v, (double[])pos.Clone()));
        InsertSorted(v);
        if (_posPeak == null || v > _peakV) SetPeak(_points.Count - 1);
    }

    /// <summary>Replaces the peak point's reading with a re-read and re-picks the peak.</summary>
    private void ReplacePeakReading(double v)
    {
        var (old, pos) = _points[_peakIndex];
        int at = _sorted.BinarySearch(old);
        if (at >= 0) _sorted.RemoveAt(at);
        InsertSorted(v);
        _points[_peakIndex] = (v, pos);

        int best = 0;
        for (int i = 1; i < _points.Count; i++)
            if (_points[i].V > _points[best].V) best = i;
        SetPeak(best);
    }

    private void SetPeak(int index)
    {
        _peakIndex = index;
        _peakV = _points[index].V;
        _posPeak = _points[index].Pos;
    }

    private void InsertSorted(double v)
    {
        int at = _sorted.BinarySearch(v);
        _sorted.Insert(at < 0 ? ~at : at, v);
    }

    /// <summary>
    /// True (and logs) when <paramref name="v"/> exceeds the absolute <c>ExitAboveV</c> stop level.
    /// Every earlier reading was below it, so this point is also the peak the caller parks at.
    /// </summary>
    private bool CheckExit(double v, string label)
    {
        if (_config.ExitAboveV <= 0 || v <= _config.ExitAboveV) return false;
        _exitHit = true;
        _log(LogLevel.Info, $"Grid search: {v:F6} V > exit {_config.ExitAboveV:0.###} V at {label} — stopping search.");
        return true;
    }

    /// <summary>Prefixes an X-step label with the Y row (Stage 2); returns the X label alone for Stage 1.</summary>
    private static string RowLabel(string rowLabel, string xLabel)
        => string.IsNullOrEmpty(rowLabel) ? xLabel : $"{rowLabel} {xLabel}";

    /// <summary>
    /// Stage 2: step Y+ row by row from the captured centre (never −Y) and run an X line scan
    /// (Stage-1 params) at each row. Returns the peak pose of the first row that clears the detect
    /// level (hand-off point), or null if no row did. The peak tracker keeps the best point so the
    /// caller can park there on a total miss.
    /// </summary>
    private async Task<double[]?> GridSearchAsync(CancellationToken ct)
    {
        int rows = _config.GridYRows;
        int ay = _config.GridYAxisIndex;
        double yStepMm = _config.GridStepUm / 1000.0;
        double cy = _posCentre[ay];

        // Bounded-loop guard (Power of 10 Rule 2): rows × (apex + LineSteps) must stay under the cap.
        long totalNodes = (long)rows * (_config.LineSteps + 1);
        if (totalNodes > _config.MaxGridNodes)
            throw new InvalidOperationException(
                $"Stage 2 {rows} Y rows × {_config.LineSteps + 1} X samples = {totalNodes} nodes exceeds the " +
                $"{_config.MaxGridNodes}-node cap — reduce Y rows / line steps or raise the cap.");

        _log(LogLevel.Info,
            $"Grid search Stage 2: {rows} Y+ rows at {_config.GridStepUm:0.#} µm pitch, X line scan each row.");

        for (int iy = 0; iy < rows; iy++)
        {
            ct.ThrowIfCancellationRequested();

            double yUm = iy * _config.GridStepUm;
            double[] rowCentre = WithAxis(_posCentre, ay, cy + (iy * yStepMm));
            bool seen = await LineSearchAsync(rowCentre, GridSearchPhase.Grid, $"Y+{yUm:0.#}µm", ySweep: false, ct).ConfigureAwait(false);
            if (seen)
            {
                _log(LogLevel.Info, $"Grid search: signal in the Y+{yUm:0.#} µm row = {_peakV:F6} V — handing off to hill climb.");
                return _posPeak;
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
