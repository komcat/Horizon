namespace Horizon.Core.Scanning;

/// <summary>
/// Parameters for a single scan pass (one step-size level).
/// </summary>
public sealed class ScanParameters
{
    /// <summary>Step sizes in mm, executed sequentially (coarse→fine).</summary>
    public required double[] StepSizes { get; init; }

    /// <summary>Axes to scan, in order (e.g. "Z","X","Y").</summary>
    public required string[] Axes { get; init; }

    /// <summary>Settle time in ms after each move.</summary>
    public int SettleTimeMs { get; init; } = 300;

    /// <summary>Number of consecutive measurement decreases before stopping.</summary>
    public int ConsecutiveDecreasesLimit { get; init; } = 3;

    /// <summary>Fractional drop from peak that triggers immediate stop (0.10 = 10%).</summary>
    public double SignificantDecreaseThreshold { get; init; } = 0.10;

    /// <summary>Maximum total distance in mm to travel from start in one direction.</summary>
    public double MaxTotalDistanceMm { get; init; } = 5.0;

    /// <summary>Minimum improvement fraction to trigger a return to global peak (0.02 = 2%).</summary>
    public double ImprovementThreshold { get; init; } = 0.02;

    /// <summary>Number of measurement samples to average per step.</summary>
    public int AveragingSamples { get; init; } = 3;

    /// <summary>
    /// Optional exit threshold. If set, the scan stops early when a measurement
    /// reaches or exceeds this value. Null means scan until the end.
    /// </summary>
    public double? ExitThreshold { get; init; }

    public static ScanParameters Coarse() => new()
    {
        StepSizes = [0.005, 0.002, 0.001, 0.0005],
        Axes = ["Z", "X", "Y"],
        SettleTimeMs = 300,
        ConsecutiveDecreasesLimit = 1,
        SignificantDecreaseThreshold = 0.10,
        MaxTotalDistanceMm = 5.0,
        ImprovementThreshold = 0.02,
        AveragingSamples = 3
    };

    public static ScanParameters Fine() => new()
    {
        StepSizes = [0.002, 0.001, 0.0005, 0.0001],
        Axes = ["Z", "X", "Y"],
        SettleTimeMs = 400,
        ConsecutiveDecreasesLimit = 1,
        SignificantDecreaseThreshold = 0.05,
        MaxTotalDistanceMm = 3.0,
        ImprovementThreshold = 0.01,
        AveragingSamples = 5
    };
}

/// <summary>
/// A single measurement data point recorded during a scan.
/// </summary>
public sealed record ScanDataPoint
{
    public required int StepIndex { get; init; }
    public required string Axis { get; init; }
    public required int Direction { get; init; }
    public required double Position { get; init; }
    public required double Value { get; init; }
    public required bool IsPeak { get; init; }
    public required double StepSize { get; init; }
}

/// <summary>
/// Results from a completed (or cancelled) scan.
/// </summary>
public sealed class ScanResults
{
    public double BaselineValue { get; set; }
    public double PeakValue { get; set; }
    public double PeakX { get; set; }
    public double PeakY { get; set; }
    public double PeakZ { get; set; }
    public bool Completed { get; set; }
    public bool Cancelled { get; set; }
    public List<ScanDataPoint> DataPoints { get; } = new();
    public TimeSpan Elapsed { get; set; }
}
