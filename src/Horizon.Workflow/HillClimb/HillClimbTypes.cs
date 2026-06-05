using Horizon.Engine;

namespace Horizon.Workflow.HillClimb;

public enum HillClimbState
{
    Idle,
    RecordingBaseline,
    Climbing,
    MovingToPeak,
    Completed,
    Cancelled,
    Error
}

public enum HillClimbStopReason
{
    ZBudgetReached,
    Converged,
    TargetReached,
    Cancelled,
    Error
}

public enum HillClimbPhase
{
    Baseline,
    ZAdvance,
    ZBacktrack,
    XCorrection,
    YCorrection,
    FinalReturn
}

public sealed class HillClimbStateChangedEventArgs : EventArgs
{
    public HillClimbState OldState { get; }
    public HillClimbState NewState { get; }
    public DateTime Timestamp { get; }

    public HillClimbStateChangedEventArgs(HillClimbState oldState, HillClimbState newState)
    {
        OldState = oldState;
        NewState = newState;
        Timestamp = DateTime.Now;
    }
}

public sealed class HillClimbBaselineRecordedEventArgs : EventArgs
{
    public double VStart { get; }
    public double Sigma { get; }
    public double EffectiveThreshold { get; }

    public HillClimbBaselineRecordedEventArgs(double vStart, double sigma, double effectiveThreshold)
    {
        VStart = vStart;
        Sigma = sigma;
        EffectiveThreshold = effectiveThreshold;
    }
}

public sealed class HillClimbStepEventArgs : EventArgs
{
    public HillClimbPhase Phase { get; }
    public int AxisIndex { get; }
    public int Direction { get; }       // +1 or -1
    public double StepSize_um { get; }
    public double VBefore { get; }
    public double VAfter { get; }
    public bool ImprovedGlobal { get; }
    public double ZCumulative_um { get; }
    public DateTime Timestamp { get; }

    public HillClimbStepEventArgs(HillClimbPhase phase, int axisIndex, int direction,
        double stepSize_um, double vBefore, double vAfter, bool improvedGlobal, double zCumulative_um)
    {
        Phase = phase;
        AxisIndex = axisIndex;
        Direction = direction;
        StepSize_um = stepSize_um;
        VBefore = vBefore;
        VAfter = vAfter;
        ImprovedGlobal = improvedGlobal;
        ZCumulative_um = zCumulative_um;
        Timestamp = DateTime.Now;
    }
}

public sealed class HillClimbLoopEventArgs : EventArgs
{
    public int LoopIndex { get; }
    public double ZTraveledThisLoop_um { get; }
    public double ZCumulative_um { get; }
    public double VLoopStart { get; }
    public double VLoopBest { get; }
    public double VPeakGlobal { get; }
    public bool LoopImproved { get; }

    public HillClimbLoopEventArgs(int loopIndex, double zTraveledThisLoop_um, double zCumulative_um,
        double vLoopStart, double vLoopBest, double vPeakGlobal, bool loopImproved)
    {
        LoopIndex = loopIndex;
        ZTraveledThisLoop_um = zTraveledThisLoop_um;
        ZCumulative_um = zCumulative_um;
        VLoopStart = vLoopStart;
        VLoopBest = vLoopBest;
        VPeakGlobal = vPeakGlobal;
        LoopImproved = loopImproved;
    }
}

public sealed class HillClimbProgressEventArgs : EventArgs
{
    public double ZCumulative_um { get; }
    public double VCurrent { get; }
    public double VPeakGlobal { get; }
    public double CurrentZStep_um { get; }
    public double CurrentXYStep_um { get; }

    public HillClimbProgressEventArgs(double zCumulative_um, double vCurrent, double vPeakGlobal,
        double currentZStep_um, double currentXYStep_um)
    {
        ZCumulative_um = zCumulative_um;
        VCurrent = vCurrent;
        VPeakGlobal = vPeakGlobal;
        CurrentZStep_um = currentZStep_um;
        CurrentXYStep_um = currentXYStep_um;
    }
}

/// <summary>Outcome of a hill-climb run; the hexapod ends at <see cref="PosPeakGlobal"/>.</summary>
public sealed class HillClimbResult
{
    public HillClimbStopReason StopReason { get; set; }
    public double VStart { get; set; }
    public double VPeakGlobal { get; set; }
    public MotionPosition? PosPeakGlobal { get; set; }
    public double ZTotalTraveled_um { get; set; }
    public int LoopsExecuted { get; set; }
    public double BaselineSigma { get; set; }
    public double EffectiveThreshold { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}
