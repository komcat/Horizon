using Horizon.Engine;

namespace Horizon.Workflow.Scanning;

/// <summary>
/// The algorithm↔hardware seam — port of the legacy <c>ScanContext</c>. A scan
/// strategy talks only to this object: it moves the hexapod, reads feedback,
/// records data points, and tracks the running peak. Hardware access is via
/// <see cref="IHorizonEngine"/> facets; feedback via an
/// <see cref="IScanFeedbackSource"/>. Halt = the strategy's
/// <see cref="CancellationToken"/>.
/// </summary>
public sealed class ScanContext
{
    private readonly IHorizonEngine _engine;
    private readonly IScanFeedbackSource _feedback;
    private readonly IProgress<ScanProgress>? _progress;
    private readonly Action<LogLevel, string> _log;
    private readonly List<ScanDataPoint> _dataPoints = new();
    private readonly List<AxisScanInfo> _axisScans = new();
    private double _lastFraction;

    public ScanContext(
        IHorizonEngine engine,
        IScanFeedbackSource feedback,
        string device,
        ScanParameters parameters,
        IProgress<ScanProgress>? progress,
        Action<LogLevel, string> log)
    {
        _engine = engine;
        _feedback = feedback;
        Device = device;
        Parameters = parameters;
        _progress = progress;
        _log = log;
    }

    public string Device { get; }
    public ScanParameters Parameters { get; }

    /// <summary>Unit of the feedback signal, e.g. <c>"A"</c>, <c>"V"</c>.</summary>
    public string Unit => _feedback.Unit;

    /// <summary>Total feedback samples received from the stream so far.</summary>
    public long FeedbackSampleCount => _feedback.SampleCount;

    public PeakInfo GlobalPeak { get; private set; } = PeakInfo.None;
    public BaselineInfo Baseline { get; set; } = BaselineInfo.None;

    // Progress state — set by the orchestrator and strategy.
    public ScanState State { get; set; } = ScanState.Idle;
    public string CurrentAxis { get; set; } = string.Empty;
    public double CurrentStepSize { get; set; }

    public IReadOnlyList<ScanDataPoint> DataPoints => _dataPoints;
    public IReadOnlyList<AxisScanInfo> AxisScans => _axisScans;

    // --- Motion ---

    /// <summary>Moves all axes to an absolute position. Returns false on a motion error.</summary>
    public async Task<bool> MoveToPositionAsync(MotionPosition target, CancellationToken ct)
    {
        var result = await _engine.Motion.MoveToPositionAsync(Device, target, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            _log(LogLevel.Warning, $"Scan move failed: {result.Message}");
        return result.IsSuccess;
    }

    /// <summary>Moves a single axis by a relative distance. Returns false on a motion error.</summary>
    public async Task<bool> MoveRelativeAsync(string axis, double distance, CancellationToken ct)
    {
        var result = await _engine.Motion.MoveRelativeAsync(Device, axis, distance, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            _log(LogLevel.Warning, $"Scan relative move failed: {result.Message}");
        return result.IsSuccess;
    }

    /// <summary>
    /// Reads the live hexapod position. Falls back to the last polled state
    /// only when that state is valid — it never fabricates (0,0,0), because a
    /// scan move issued against a zero target would slam the stage to its
    /// absolute origin. If the position cannot be trusted, the scan aborts.
    /// </summary>
    public async Task<MotionPosition> GetCurrentPositionAsync(CancellationToken ct)
    {
        var result = await _engine.Motion.GetPositionAsync(Device).ConfigureAwait(false);
        if (result.IsSuccess)
            return result.Value;

        var state = _engine.Motion.GetState(Device);
        if (state.Valid)
        {
            _log(LogLevel.Warning,
                $"Live position read failed ({result.Message}) — using last polled state.");
            return state.Position;
        }

        throw new InvalidOperationException(
            $"Cannot read {Device} position ({result.Message}) — aborting scan to avoid an unsafe move.");
    }

    // --- Measurement ---

    /// <summary>One feedback reading. Port of the legacy <c>GetMeasurement</c>.</summary>
    public Task<double> GetMeasurementAsync(CancellationToken ct) => _feedback.ReadAsync(ct);

    /// <summary>Averaged feedback reading. Port of the legacy <c>GetAveragedMeasurement</c>.</summary>
    public Task<double> GetAveragedMeasurementAsync(int samples, CancellationToken ct)
        => _feedback.ReadAveragedAsync(samples, ct);

    // --- Timing ---

    /// <summary>Settle dwell after a move. Throws if cancelled (the halt signal).</summary>
    public Task SettleAsync(CancellationToken ct) => Task.Delay(Parameters.MotionSettleMs, ct);

    /// <summary>Generic interruptible delay. Throws if cancelled.</summary>
    public Task DelayAsync(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);

    // --- Data & peak tracking ---

    public void RecordDataPoint(ScanDataPoint dataPoint) => _dataPoints.Add(dataPoint);

    public void AddAxisScan(AxisScanInfo axisScan) => _axisScans.Add(axisScan);

    /// <summary>Updates the global peak when <paramref name="value"/> beats it. Returns true if it did.</summary>
    public bool UpdateGlobalPeak(double value, MotionPosition position, string context)
    {
        if (!GlobalPeak.Valid || value > GlobalPeak.Value)
        {
            GlobalPeak = new PeakInfo(value, position, context, true);
            return true;
        }
        return false;
    }

    // --- Noise / significance ---

    /// <summary>Sample standard deviation of all recorded values — the noise-floor estimate.</summary>
    public double NoiseSigma()
    {
        if (_dataPoints.Count < 2) return 0;

        double mean = 0;
        for (int i = 0; i < _dataPoints.Count; i++) mean += _dataPoints[i].Value;
        mean /= _dataPoints.Count;

        double sumSq = 0;
        for (int i = 0; i < _dataPoints.Count; i++)
        {
            double d = _dataPoints[i].Value - mean;
            sumSq += d * d;
        }
        return Math.Sqrt(sumSq / (_dataPoints.Count - 1));
    }

    /// <summary>
    /// Peak signal-to-noise ratio: (peak − baseline) / σ. Returns 0 when there
    /// is no peak/baseline yet, or +∞ when σ is zero but the peak rose.
    /// </summary>
    public double PeakSnr()
    {
        if (!GlobalPeak.Valid || !Baseline.Valid) return 0;
        double sigma = NoiseSigma();
        double rise = GlobalPeak.Value - Baseline.Value;
        if (sigma <= 0) return rise > 0 ? double.PositiveInfinity : 0;
        return rise / sigma;
    }

    /// <summary>True when the peak clears the noise gate (<see cref="ScanParameters.MinPeakSigma"/>).</summary>
    public bool PeakIsSignificant() => PeakSnr() >= Parameters.MinPeakSigma;

    // --- No-signal early abort ---

    /// <summary>Set when the scan stopped early because no signal rose above the noise.</summary>
    public bool NoSignalAbort { get; private set; }

    /// <summary>Why the scan aborted early; empty when it did not.</summary>
    public string AbortReason { get; private set; } = string.Empty;

    /// <summary>Flags a no-signal early abort — the strategy then unwinds and the scan ends.</summary>
    public void FlagNoSignalAbort(string reason)
    {
        NoSignalAbort = true;
        AbortReason = reason;
    }

    // --- Progress & logging ---

    /// <summary>Reports a new phase fraction (0..1) with a message.</summary>
    public void ReportPhase(double fraction, string message)
    {
        _lastFraction = fraction;
        Emit(message, GlobalPeak.Valid ? GlobalPeak.Value : 0);
    }

    /// <summary>Reports an in-phase measurement update, keeping the last fraction.</summary>
    public void ReportSample(string message, double currentValue) => Emit(message, currentValue);

    private void Emit(string message, double currentValue)
        => _progress?.Report(new ScanProgress(
            _lastFraction, message, State, CurrentAxis, CurrentStepSize,
            _dataPoints.Count, currentValue, GlobalPeak.Valid ? GlobalPeak.Value : 0,
            _feedback.SampleCount));

    public void Log(LogLevel level, string message) => _log(level, message);
}
