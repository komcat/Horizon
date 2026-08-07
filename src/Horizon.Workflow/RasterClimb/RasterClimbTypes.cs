using Horizon.Engine;

namespace Horizon.Workflow.RasterClimb;

public enum RasterClimbState
{
    Idle,
    SteppingZ,
    Rastering,
    MovingToPlaneBest,
    MovingToBest,
    Completed,
    Cancelled,
    Error
}

public enum RasterClimbStopReason
{
    /// <summary>A plane's best reached the target fraction — the intended exit.</summary>
    TargetReached,
    /// <summary>The Z travel budget ran out before the target was reached.</summary>
    ZBudgetSpent,
    /// <summary>
    /// A plane's best collapsed below the peak by more than the contact-drop fraction —
    /// treated as the FAU having hit something. The climb stops and retreats to the peak.
    /// </summary>
    SignalDropped,
    Cancelled,
    Error
}

public sealed class RasterClimbStateChangedEventArgs : EventArgs
{
    public RasterClimbState OldState { get; }
    public RasterClimbState NewState { get; }
    public DateTime Timestamp { get; }

    public RasterClimbStateChangedEventArgs(RasterClimbState oldState, RasterClimbState newState)
    {
        OldState = oldState;
        NewState = newState;
        Timestamp = DateTime.Now;
    }
}

/// <summary>One sampled raster point. Offsets are relative to the plane's centre (where the raster began).</summary>
public sealed class RasterClimbPointEventArgs : EventArgs
{
    public int PlaneIndex { get; }
    public int PointIndex { get; }
    public double OffsetA_um { get; }
    public double OffsetB_um { get; }
    public double V { get; }
    public bool IsPlaneBest { get; }

    public RasterClimbPointEventArgs(int planeIndex, int pointIndex,
        double offsetA_um, double offsetB_um, double v, bool isPlaneBest)
    {
        PlaneIndex = planeIndex;
        PointIndex = pointIndex;
        OffsetA_um = offsetA_um;
        OffsetB_um = offsetB_um;
        V = v;
        IsPlaneBest = isPlaneBest;
    }
}

/// <summary>
/// A completed Z plane: its raster is finished and the stage has been moved back to the
/// plane's best XY. This is the first moment the plane's signal is actually known — the
/// whole point of the algorithm.
/// </summary>
public sealed class RasterClimbPlaneEventArgs : EventArgs
{
    public int PlaneIndex { get; }
    /// <summary>Cumulative Z travel from the start of the run (µm, signed by ZSign).</summary>
    public double ZOffset_um { get; }
    public double PlaneBestV { get; }
    public double BestOffsetA_um { get; }
    public double BestOffsetB_um { get; }
    /// <summary>Best-so-far across every plane rastered in this run.</summary>
    public double RunBestV { get; }
    /// <summary>Fraction of the target the plane's best reached (1.0 = at target).</summary>
    public double TargetFraction { get; }

    public RasterClimbPlaneEventArgs(int planeIndex, double zOffset_um, double planeBestV,
        double bestOffsetA_um, double bestOffsetB_um, double runBestV, double targetFraction)
    {
        PlaneIndex = planeIndex;
        ZOffset_um = zOffset_um;
        PlaneBestV = planeBestV;
        BestOffsetA_um = bestOffsetA_um;
        BestOffsetB_um = bestOffsetB_um;
        RunBestV = runBestV;
        TargetFraction = targetFraction;
    }
}

public sealed class RasterClimbProgressEventArgs : EventArgs
{
    public int PlaneIndex { get; }
    public int PointsInPlane { get; }
    public int PointsPerPlane { get; }
    public double ZOffset_um { get; }
    public double VCurrent { get; }
    public double VPlaneBest { get; }
    public double VRunBest { get; }

    public RasterClimbProgressEventArgs(int planeIndex, int pointsInPlane, int pointsPerPlane,
        double zOffset_um, double vCurrent, double vPlaneBest, double vRunBest)
    {
        PlaneIndex = planeIndex;
        PointsInPlane = pointsInPlane;
        PointsPerPlane = pointsPerPlane;
        ZOffset_um = zOffset_um;
        VCurrent = vCurrent;
        VPlaneBest = vPlaneBest;
        VRunBest = vRunBest;
    }
}

/// <summary>Outcome of a raster-climb run; the stage ends at <see cref="PosBest"/>.</summary>
public sealed class RasterClimbResult
{
    public RasterClimbStopReason StopReason { get; set; }
    public double VStart { get; set; }
    public double VBest { get; set; }
    public MotionPosition? PosBest { get; set; }
    /// <summary>Cumulative Z travel of the plane that produced the best signal (µm).</summary>
    public double BestZOffset_um { get; set; }
    public double BestOffsetA_um { get; set; }
    public double BestOffsetB_um { get; set; }
    /// <summary>Total Z travel commanded during the run (µm).</summary>
    public double ZTravelled_um { get; set; }
    public int PlanesRastered { get; set; }
    public int PointsSampled { get; set; }
    public double TargetV { get; set; }
    /// <summary><c>VBest / TargetV</c> — 0.70 is the default exit criterion.</summary>
    public double TargetFraction { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public string Message { get; set; } = string.Empty;
}
