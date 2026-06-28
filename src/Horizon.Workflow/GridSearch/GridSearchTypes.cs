using Horizon.Engine;

namespace Horizon.Workflow.GridSearch;

public enum GridSearchState
{
    Idle,
    RecordingBaseline,
    LineSearch,
    GridSearch,
    MovingToPeak,
    Completed,
    Cancelled,
    Error
}

/// <summary>How a grid-search run ended. The first two set <see cref="GridSearchResult.SignalFound"/>.</summary>
public enum GridSearchStopReason
{
    /// <summary>Stage 1 (X line) found signal; parked at the line peak.</summary>
    SignalFoundLine,
    /// <summary>Stage 2 (XY grid) found signal; parked at the first node over the detect level.</summary>
    SignalFoundGrid,
    /// <summary>Neither stage cleared the detect level; parked at the best point seen.</summary>
    NoSignal,
    Cancelled,
    Error
}

/// <summary>Which stage a measurement came from.</summary>
public enum GridSearchPhase
{
    Baseline,
    Line,
    Grid
}

public sealed class GridSearchStateChangedEventArgs : EventArgs
{
    public GridSearchState OldState { get; }
    public GridSearchState NewState { get; }
    public DateTime Timestamp { get; }

    public GridSearchStateChangedEventArgs(GridSearchState oldState, GridSearchState newState)
    {
        OldState = oldState;
        NewState = newState;
        Timestamp = DateTime.Now;
    }
}

public sealed class GridSearchBaselineRecordedEventArgs : EventArgs
{
    public double VBaseline { get; }
    public double Sigma { get; }
    /// <summary>The absolute level (<c>vBase + DetectDeltaV</c>) a reading must clear to count as signal.</summary>
    public double DetectLevel { get; }

    public GridSearchBaselineRecordedEventArgs(double vBaseline, double sigma, double detectLevel)
    {
        VBaseline = vBaseline;
        Sigma = sigma;
        DetectLevel = detectLevel;
    }
}

/// <summary>One sampled node/step: its value, the running peak, and whether it cleared the detect level.</summary>
public sealed class GridSearchMeasuredEventArgs : EventArgs
{
    public int Index { get; }
    public GridSearchPhase Phase { get; }
    /// <summary>A short label for the node/step, e.g. <c>"−35 µm"</c> or <c>"(−2,3)"</c>.</summary>
    public string Label { get; }
    public double V { get; }
    public double VPeak { get; }
    public bool SignalSeen { get; }
    public DateTime Timestamp { get; }

    public GridSearchMeasuredEventArgs(int index, GridSearchPhase phase, string label,
        double v, double vPeak, bool signalSeen)
    {
        Index = index;
        Phase = phase;
        Label = label;
        V = v;
        VPeak = vPeak;
        SignalSeen = signalSeen;
        Timestamp = DateTime.Now;
    }
}

/// <summary>Outcome of a grid-search run; the hexapod ends at <see cref="PosPeak"/>.</summary>
public sealed class GridSearchResult
{
    public GridSearchStopReason StopReason { get; set; }

    /// <summary>True when a stage cleared the detect level — the caller should hand off to the hill climber.</summary>
    public bool SignalFound { get; set; }

    public double VBaseline { get; set; }
    public double DetectLevel { get; set; }
    public double VPeak { get; set; }
    public MotionPosition? PosPeak { get; set; }

    /// <summary>Total nodes/steps measured across both stages.</summary>
    public int PointsMeasured { get; set; }
    public double BaselineSigma { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}
