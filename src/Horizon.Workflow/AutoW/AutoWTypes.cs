using Horizon.Engine;

namespace Horizon.Workflow.AutoW;

public enum AutoWState
{
    Idle,
    RecordingBaseline,
    PeakingXY,
    ProbingW,
    ClimbingW,
    MovingToPeak,
    Completed,
    Cancelled,
    Error
}

public enum AutoWStopReason
{
    PeakFound,
    WBudgetReached,
    Cancelled,
    Error
}

/// <summary>Which part of the search a measurement came from.</summary>
public enum AutoWPhase
{
    Baseline,
    ProbeForward,
    ProbeReverse,
    Climb
}

public sealed class AutoWStateChangedEventArgs : EventArgs
{
    public AutoWState OldState { get; }
    public AutoWState NewState { get; }
    public DateTime Timestamp { get; }

    public AutoWStateChangedEventArgs(AutoWState oldState, AutoWState newState)
    {
        OldState = oldState;
        NewState = newState;
        Timestamp = DateTime.Now;
    }
}

public sealed class AutoWBaselineRecordedEventArgs : EventArgs
{
    /// <summary>The XY-peaked baseline score (T0).</summary>
    public double T0 { get; }
    public double Sigma { get; }
    public double EffectiveThreshold { get; }
    public double WStartDeg { get; }

    public AutoWBaselineRecordedEventArgs(double t0, double sigma, double effectiveThreshold, double wStartDeg)
    {
        T0 = t0;
        Sigma = sigma;
        EffectiveThreshold = effectiveThreshold;
        WStartDeg = wStartDeg;
    }
}

/// <summary>One W candidate measured: its XY-peaked score and where it sits in the search.</summary>
public sealed class AutoWMeasuredEventArgs : EventArgs
{
    public int Index { get; }
    public AutoWPhase Phase { get; }
    /// <summary>Cumulative W offset from the start angle (deg).</summary>
    public double WOffsetDeg { get; }
    /// <summary>The XY-peaked score at this W (T).</summary>
    public double T { get; }
    /// <summary>Best XY-peaked score seen so far.</summary>
    public double TPeakGlobal { get; }
    public bool ImprovedGlobal { get; }
    public DateTime Timestamp { get; }

    public AutoWMeasuredEventArgs(int index, AutoWPhase phase, double wOffsetDeg,
        double t, double tPeakGlobal, bool improvedGlobal)
    {
        Index = index;
        Phase = phase;
        WOffsetDeg = wOffsetDeg;
        T = t;
        TPeakGlobal = tPeakGlobal;
        ImprovedGlobal = improvedGlobal;
        Timestamp = DateTime.Now;
    }
}

/// <summary>Outcome of an Auto-W run; the hexapod ends at <see cref="PosPeakGlobal"/>.</summary>
public sealed class AutoWResult
{
    public AutoWStopReason StopReason { get; set; }
    public double T0 { get; set; }
    public double TPeakGlobal { get; set; }
    public MotionPosition? PosPeakGlobal { get; set; }
    public double WStartDeg { get; set; }
    /// <summary>W offset (deg) of the global best, relative to the start angle.</summary>
    public double WPeakOffsetDeg { get; set; }
    public int MeasurementsTaken { get; set; }
    public double BaselineSigma { get; set; }
    public double EffectiveThreshold { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}
