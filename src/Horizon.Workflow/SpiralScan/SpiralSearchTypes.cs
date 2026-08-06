using Horizon.Engine;

namespace Horizon.Workflow.SpiralScan;

public enum SpiralSearchState
{
    Idle,
    RecordingBaseline,
    Searching,
    MovingToBest,
    Completed,
    Cancelled,
    Error
}

public enum SpiralSearchStopReason
{
    RadiusReached,
    MaxPointsReached,
    TargetReached,
    Cancelled,
    Error
}

public sealed class SpiralSearchStateChangedEventArgs : EventArgs
{
    public SpiralSearchState OldState { get; }
    public SpiralSearchState NewState { get; }
    public DateTime Timestamp { get; }

    public SpiralSearchStateChangedEventArgs(SpiralSearchState oldState, SpiralSearchState newState)
    {
        OldState = oldState;
        NewState = newState;
        Timestamp = DateTime.Now;
    }
}

public sealed class SpiralSearchBaselineRecordedEventArgs : EventArgs
{
    public double VStart { get; }
    public double Sigma { get; }
    public double EffectiveThreshold { get; }

    public SpiralSearchBaselineRecordedEventArgs(double vStart, double sigma, double effectiveThreshold)
    {
        VStart = vStart;
        Sigma = sigma;
        EffectiveThreshold = effectiveThreshold;
    }
}

/// <summary>One sampled spiral grid point. Offsets are relative to the start point.</summary>
public sealed class SpiralSearchPointEventArgs : EventArgs
{
    public int PointIndex { get; }
    public double OffsetA_um { get; }
    public double OffsetB_um { get; }
    public double Radius_um { get; }
    public double V { get; }
    public bool IsBest { get; }
    public DateTime Timestamp { get; }

    public SpiralSearchPointEventArgs(int pointIndex, double offsetA_um, double offsetB_um,
        double radius_um, double v, bool isBest)
    {
        PointIndex = pointIndex;
        OffsetA_um = offsetA_um;
        OffsetB_um = offsetB_um;
        Radius_um = radius_um;
        V = v;
        IsBest = isBest;
        Timestamp = DateTime.Now;
    }
}

public sealed class SpiralSearchProgressEventArgs : EventArgs
{
    public int PointsVisited { get; }
    public double CurrentRadius_um { get; }
    public double VCurrent { get; }
    public double VBest { get; }

    public SpiralSearchProgressEventArgs(int pointsVisited, double currentRadius_um, double vCurrent, double vBest)
    {
        PointsVisited = pointsVisited;
        CurrentRadius_um = currentRadius_um;
        VCurrent = vCurrent;
        VBest = vBest;
    }
}

/// <summary>Outcome of a spiral-search run; the stage ends at <see cref="PosBest"/>.</summary>
public sealed class SpiralSearchResult
{
    public SpiralSearchStopReason StopReason { get; set; }
    public double VStart { get; set; }
    public double VBest { get; set; }
    public MotionPosition? PosBest { get; set; }
    public double BestOffsetA_um { get; set; }
    public double BestOffsetB_um { get; set; }
    public double BestRadius_um { get; set; }
    public int PointsVisited { get; set; }
    public double BaselineSigma { get; set; }
    public double EffectiveThreshold { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}
