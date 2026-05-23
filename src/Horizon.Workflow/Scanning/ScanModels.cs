using Horizon.Common;
using Horizon.Engine;

namespace Horizon.Workflow.Scanning;

/// <summary>State-machine phases of a hexapod scan. Port of the legacy <c>ScanState</c>.</summary>
public enum ScanState
{
    Idle,
    Initializing,
    RecordingBaseline,
    Scanning,
    ReturningToPeak,
    Completed,
    Error,
    Halted,
}

/// <summary>Why a single-direction scan stopped. Port of the legacy <c>TerminationReason</c>.</summary>
public enum TerminationReason
{
    ConsecutiveDecreases,
    SignificantDecrease,
    MaxDistanceReached,
    MotionError,
    UserHalt,
}

/// <summary>
/// Preset step-size schedules. Port of the legacy <c>ScanMode</c> /
/// <c>ScanExecutor::createScanParameters</c>.
/// </summary>
public enum ScanMode
{
    /// <summary>5 → 2 → 1 → 0.5 µm, ±0.2 mm range.</summary>
    Coarse,
    /// <summary>2 → 1 → 0.5 µm, ±0.2 mm range.</summary>
    Fine,
    /// <summary>0.5 → 0.2 → 0.1 µm, ±0.1 mm range.</summary>
    UltraFine,
}

/// <summary>
/// Configuration for a coarse-to-fine hexapod scan. Port of the legacy
/// <c>ScanParameters</c>. All distances are in millimetres (matching
/// <see cref="IMotionFacet"/>).
/// </summary>
public sealed record ScanParameters
{
    /// <summary>Axes scanned, in order. Legacy default: Z, then Y, then X.</summary>
    public IReadOnlyList<string> AxesToScan { get; init; } = new[] { "Z", "Y", "X" };

    /// <summary>Step sizes (mm), applied in list order — coarse → fine. Legacy Coarse default.</summary>
    public IReadOnlyList<double> StepSizes { get; init; } = new[] { 0.005, 0.002, 0.001, 0.0005 };

    /// <summary>Dwell after each move before measuring (ms).</summary>
    public int MotionSettleMs { get; init; } = 400;

    /// <summary>
    /// Scan motion velocity in mm/s. Applied controller-aware: a PI hexapod
    /// gets the system (path) velocity; an ACS gantry gets every axis.
    /// 0 = leave the controller's current velocity unchanged.
    /// </summary>
    public double ScanVelocityMmPerSec { get; init; } = 1.0;

    /// <summary>Stop a direction after this many consecutive non-improving steps.</summary>
    public int ConsecutiveDecreasesLimit { get; init; } = 3;

    /// <summary>Stop a direction immediately on a relative drop larger than this (0..1).</summary>
    public double SignificantDecreaseThreshold { get; init; } = 0.05;

    /// <summary>
    /// Maximum travel per direction before giving up (mm). Legacy: 0.2 mm
    /// standard, 0.1 mm ultra-fine — a tight bound that keeps a noisy scan from
    /// wandering indefinitely.
    /// </summary>
    public double MaxTotalDistanceMm { get; init; } = 0.2;

    /// <summary>Return to the global peak between axes when it beats the current reading by this fraction.</summary>
    public double ImprovementThreshold { get; init; } = 0.01;

    /// <summary>Samples averaged when recording the baseline.</summary>
    public int AveragingCount { get; init; } = 3;

    /// <summary>Hard cap on a single step size (mm) — a safety limit.</summary>
    public double MaxStepSizeMm { get; init; } = 0.5;

    /// <summary>Overall scan timeout.</summary>
    public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Noise gate, in standard deviations. A result counts as a real peak only
    /// when (peak − baseline) exceeds this many σ of the measured samples; the
    /// same test, applied after the first axis, aborts a no-signal scan early.
    /// </summary>
    public double MinPeakSigma { get; init; } = 3.0;

    /// <summary>
    /// Builds the parameter set for a preset mode. Port of the legacy
    /// <c>ScanExecutor::createScanParameters</c> — only the step schedule and
    /// per-direction range differ between modes.
    /// </summary>
    public static ScanParameters ForMode(ScanMode mode) => mode switch
    {
        ScanMode.Coarse => new ScanParameters
        {
            StepSizes = new[] { 0.005, 0.002, 0.001, 0.0005 },
            MaxTotalDistanceMm = 0.2,
        },
        ScanMode.Fine => new ScanParameters
        {
            StepSizes = new[] { 0.002, 0.001, 0.0005 },
            MaxTotalDistanceMm = 0.2,
        },
        ScanMode.UltraFine => new ScanParameters
        {
            StepSizes = new[] { 0.0005, 0.0002, 0.0001 },
            MaxTotalDistanceMm = 0.1,
        },
        _ => new ScanParameters(),
    };

    /// <summary>Validates the parameter set. Port of the legacy <c>ScanParameters::Validate</c>.</summary>
    public Result Validate()
    {
        if (StepSizes.Count == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "At least one step size must be specified.");
        if (AxesToScan.Count == 0)
            return Result.Fail(ErrorCode.InvalidParameter, "At least one axis must be specified.");

        foreach (var step in StepSizes)
        {
            if (step <= 0 || step > MaxStepSizeMm)
                return Result.Fail(ErrorCode.InvalidParameter,
                    $"Step size {step} mm is invalid — must be between 0 and {MaxStepSizeMm} mm.");
        }

        if (MotionSettleMs < 0)
            return Result.Fail(ErrorCode.InvalidParameter, "Motion settle time cannot be negative.");
        if (ConsecutiveDecreasesLimit < 1)
            return Result.Fail(ErrorCode.InvalidParameter, "Consecutive decreases limit must be at least 1.");
        if (ImprovementThreshold is < 0 or > 1)
            return Result.Fail(ErrorCode.InvalidParameter, "Improvement threshold must be between 0 and 1.");
        if (SignificantDecreaseThreshold is < 0 or > 1)
            return Result.Fail(ErrorCode.InvalidParameter, "Significant decrease threshold must be between 0 and 1.");
        if (AveragingCount < 1)
            return Result.Fail(ErrorCode.InvalidParameter, "Averaging count must be at least 1.");
        if (MinPeakSigma < 0)
            return Result.Fail(ErrorCode.InvalidParameter, "Min peak σ cannot be negative.");

        return Result.Ok();
    }
}

/// <summary>One measurement taken during a scan. Port of the legacy <c>ScanDataPoint</c>.</summary>
public sealed record ScanDataPoint
{
    public double Value { get; init; }
    public MotionPosition Position { get; init; } = MotionPosition.Zero;
    public DateTime Timestamp { get; init; }
    public string Axis { get; init; } = string.Empty;
    public double StepSize { get; init; }
    /// <summary>+1 or -1.</summary>
    public int Direction { get; init; }
    public double Gradient { get; init; }
    public double RelativeChange { get; init; }
    /// <summary>True when this point set a new local maximum.</summary>
    public bool IsPeak { get; init; }
}

/// <summary>The pre-scan reading. Port of the legacy <c>BaselineInfo</c>.</summary>
public sealed record BaselineInfo(double Value, MotionPosition Position, int SampleCount, bool Valid)
{
    public static readonly BaselineInfo None = new(0, MotionPosition.Zero, 0, false);
}

/// <summary>The best reading found and where. Port of the legacy <c>PeakInfo</c>.</summary>
public sealed record PeakInfo(double Value, MotionPosition Position, string Context, bool Valid)
{
    public static readonly PeakInfo None = new(0, MotionPosition.Zero, string.Empty, false);
}

/// <summary>Summary of one axis pass. Port of the legacy <c>AxisScanInfo</c>.</summary>
public sealed record AxisScanInfo
{
    public string Axis { get; init; } = string.Empty;
    public double StepSize { get; init; }
    public double FinalValue { get; init; }
    public MotionPosition FinalPosition { get; init; } = MotionPosition.Zero;
    public int TotalMeasurements { get; init; }
}

/// <summary>The complete outcome of a scan. Port of the legacy <c>ScanResults</c>.</summary>
public sealed record ScanResult
{
    public string Device { get; init; } = string.Empty;
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public TimeSpan Duration => EndTime - StartTime;
    public ScanState FinalState { get; init; }
    public BaselineInfo Baseline { get; init; } = BaselineInfo.None;
    public PeakInfo Peak { get; init; } = PeakInfo.None;
    public IReadOnlyList<ScanDataPoint> Measurements { get; init; } = Array.Empty<ScanDataPoint>();
    public IReadOnlyList<AxisScanInfo> AxisScans { get; init; } = Array.Empty<AxisScanInfo>();
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Sample standard deviation of all scan measurements — the noise floor.</summary>
    public double NoiseSigma { get; init; }

    /// <summary>(peak − baseline) / noise σ — higher means more clearly a real peak.</summary>
    public double PeakSnr { get; init; }

    /// <summary>True when the peak rose clear of the noise (<see cref="PeakSnr"/> ≥ MinPeakSigma).</summary>
    public bool PeakIsSignificant { get; init; }
}

/// <summary>Progress snapshot raised through <see cref="IProgress{T}"/> while a scan runs.</summary>
public sealed record ScanProgress(
    double Fraction,
    string Message,
    ScanState State,
    string CurrentAxis,
    double CurrentStepSize,
    int MeasurementCount,
    double CurrentValue,
    double PeakValue,
    long FeedbackSampleCount);
